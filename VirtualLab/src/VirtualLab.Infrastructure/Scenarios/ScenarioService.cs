using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using VirtualLab.Application.Callbacks;
using VirtualLab.Application.Forecourt;
using VirtualLab.Application.PreAuth;
using VirtualLab.Application.Scenarios;
using VirtualLab.Domain.Enums;
using VirtualLab.Domain.Models;
using VirtualLab.Infrastructure.Persistence;

namespace VirtualLab.Infrastructure.Scenarios;

public sealed class ScenarioService(
    VirtualLabDbContext dbContext,
    IForecourtSimulationService forecourtSimulationService,
    IPreAuthSimulationService preAuthSimulationService,
    ICallbackCaptureService callbackCaptureService) : IScenarioService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    static ScenarioService()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public async Task<IReadOnlyList<ScenarioDefinitionView>> ListScenariosAsync(CancellationToken cancellationToken = default)
    {
        List<ScenarioDefinition> definitions = await dbContext.ScenarioDefinitions
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        List<ScenarioRun> recentRuns = await dbContext.ScenarioRuns
            .AsNoTracking()
            .Include(x => x.Site)
            .Include(x => x.ScenarioDefinition)
            .OrderByDescending(x => x.StartedAtUtc)
            .ToListAsync(cancellationToken);
        Dictionary<Guid, ScenarioRun> latestRuns = recentRuns
            .GroupBy(x => x.ScenarioDefinitionId)
            .ToDictionary(x => x.Key, x => x.First());

        return definitions
            .Select(x => MapDefinition(x, latestRuns.GetValueOrDefault(x.Id)))
            .ToArray();
    }

    public async Task<IReadOnlyList<ScenarioRunSummaryView>> ListRunsAsync(int limit, CancellationToken cancellationToken = default)
    {
        int take = Math.Clamp(limit, 1, 100);
        List<ScenarioRun> runs = await dbContext.ScenarioRuns
            .AsNoTracking()
            .Include(x => x.Site)
            .Include(x => x.ScenarioDefinition)
            .OrderByDescending(x => x.StartedAtUtc)
            .Take(take)
            .ToListAsync(cancellationToken);

        return runs.Select(MapRunSummary).ToArray();
    }

    public async Task<ScenarioRunDetailView?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        ScenarioRun? run = await dbContext.ScenarioRuns
            .AsNoTracking()
            .Include(x => x.Site)
            .Include(x => x.ScenarioDefinition)
            .SingleOrDefaultAsync(x => x.Id == runId, cancellationToken);

        return run is null ? null : MapRunDetail(run);
    }

    public async Task<ScenarioRunDetailView> RunAsync(ScenarioRunRequest request, CancellationToken cancellationToken = default)
    {
        ScenarioDefinition definition = await ResolveDefinitionAsync(request, cancellationToken);
        ScenarioScriptDefinition script = DeserializeScript(definition.DefinitionJson);
        if (string.IsNullOrWhiteSpace(script.SiteCode))
        {
            throw new InvalidOperationException($"Scenario '{definition.ScenarioKey}' does not define a siteCode.");
        }

        Site site = await dbContext.Sites
            .Include(x => x.Pumps)
                .ThenInclude(x => x.Nozzles)
            .SingleAsync(x => x.SiteCode == script.SiteCode && x.IsActive, cancellationToken);

        int replaySeed = request.ReplaySeed ?? definition.DeterministicSeed;
        string runCorrelationId = BuildRunCorrelationId(definition.ScenarioKey, replaySeed);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        ScenarioRun run = new()
        {
            Id = Guid.NewGuid(),
            SiteId = site.Id,
            ScenarioDefinitionId = definition.Id,
            CorrelationId = runCorrelationId,
            ReplaySeed = replaySeed,
            ReplaySignature = ComputeReplaySignature(definition.ScenarioKey, replaySeed, definition.DefinitionJson),
            Status = ScenarioRunStatus.Running,
            InputSnapshotJson = JsonSerializer.Serialize(
                new
                {
                    siteCode = site.SiteCode,
                    siteId = site.Id,
                    profileId = site.ActiveFccSimulatorProfileId,
                    site.DeliveryMode,
                    site.PreAuthMode,
                    replaySeed,
                    setup = script.Setup,
                },
                JsonOptions),
            ResultSummaryJson = "{}",
            StartedAtUtc = now,
        };

        dbContext.ScenarioRuns.Add(run);
        AddScenarioLog(run, site, "Information", "ScenarioRunStarted", $"Scenario '{definition.Name}' started.", run.CorrelationId, new
        {
            definition.ScenarioKey,
            replaySeed,
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        SiteSnapshot snapshot = new(site.ActiveFccSimulatorProfileId, site.DeliveryMode, site.PreAuthMode);
        ScenarioExecutionContext context = new(run, definition, script, site, replaySeed);
        context.TrackCorrelation(run.CorrelationId);

        try
        {
            await ApplySetupAsync(site, script.Setup, cancellationToken);

            for (int index = 0; index < script.Actions.Count; index++)
            {
                ScenarioActionDefinition action = script.Actions[index];
                ScenarioStepResultView step = await ExecuteActionAsync(index + 1, action, context, cancellationToken);
                context.Steps.Add(step);

                AddScenarioLog(
                    run,
                    site,
                    step.Status == "Succeeded" ? "Information" : "Warning",
                    step.Status == "Succeeded" ? "ScenarioStepCompleted" : "ScenarioStepFailed",
                    step.Message,
                    string.IsNullOrWhiteSpace(step.CorrelationId) ? run.CorrelationId : step.CorrelationId,
                    new
                    {
                        step.Order,
                        step.Kind,
                        step.Name,
                        step.Status,
                        output = DeserializeJsonObject(step.OutputJson),
                    });

                await AttachArtifactsAsync(site.Id, run.Id, context.KnownCorrelationIds, cancellationToken);

                if (!string.Equals(step.Status, "Succeeded", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ScenarioRunFailedException(step.Message);
                }
            }

            for (int index = 0; index < script.Assertions.Count; index++)
            {
                ScenarioAssertionDefinition assertion = script.Assertions[index];
                ScenarioAssertionResultView result = await EvaluateAssertionAsync(index + 1, assertion, context, cancellationToken);
                context.Assertions.Add(result);

                AddScenarioLog(
                    run,
                    site,
                    result.Passed ? "Information" : "Warning",
                    result.Passed ? "ScenarioAssertionPassed" : "ScenarioAssertionFailed",
                    result.Message,
                    run.CorrelationId,
                    new
                    {
                        result.Order,
                        result.Kind,
                        result.Name,
                        result.Passed,
                        output = DeserializeJsonObject(result.OutputJson),
                    });
            }

            run.Status = context.Assertions.All(x => x.Passed) ? ScenarioRunStatus.Completed : ScenarioRunStatus.Failed;
        }
        catch (Exception exception) when (exception is not ScenarioRunFailedException)
        {
            run.Status = ScenarioRunStatus.Failed;
            context.Errors.Add(exception.Message);
            AddScenarioLog(run, site, "Error", "ScenarioRunFailed", exception.Message, run.CorrelationId, null);
        }
        catch (ScenarioRunFailedException exception)
        {
            run.Status = ScenarioRunStatus.Failed;
            context.Errors.Add(exception.Message);
            AddScenarioLog(run, site, "Warning", "ScenarioRunFailed", exception.Message, run.CorrelationId, null);
        }
        finally
        {
            await RestoreSetupAsync(site, snapshot, cancellationToken);
            await AttachArtifactsAsync(site.Id, run.Id, context.KnownCorrelationIds, cancellationToken);

            run.CompletedAtUtc = DateTimeOffset.UtcNow;
            run.ResultSummaryJson = JsonSerializer.Serialize(
                new
                {
                    siteCode = site.SiteCode,
                    siteId = site.Id,
                    definition.ScenarioKey,
                    definition.Name,
                    run.Status,
                    replaySeed,
                    steps = context.Steps,
                    assertions = context.Assertions,
                    errors = context.Errors,
                    transactionCount = await dbContext.SimulatedTransactions.CountAsync(x => x.ScenarioRunId == run.Id, cancellationToken),
                    preAuthCount = await dbContext.PreAuthSessions.CountAsync(x => x.ScenarioRunId == run.Id, cancellationToken),
                    logCount = await dbContext.LabEventLogs.CountAsync(x => x.ScenarioRunId == run.Id, cancellationToken),
                },
                JsonOptions);

            AddScenarioLog(
                run,
                site,
                run.Status == ScenarioRunStatus.Completed ? "Information" : "Warning",
                run.Status == ScenarioRunStatus.Completed ? "ScenarioRunCompleted" : "ScenarioRunCompletedWithFailures",
                $"Scenario '{definition.Name}' finished with status {run.Status}.",
                run.CorrelationId,
                new
                {
                    stepCount = context.Steps.Count,
                    assertionCount = context.Assertions.Count,
                    errorCount = context.Errors.Count,
                });

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return await GetRunAsync(run.Id, cancellationToken)
            ?? throw new InvalidOperationException("Scenario run could not be reloaded.");
    }

    public async Task<ScenarioImportResult> ImportAsync(ScenarioImportRequest request, CancellationToken cancellationToken = default)
    {
        LabEnvironment environment = await dbContext.LabEnvironments
            .OrderBy(x => x.CreatedAtUtc)
            .FirstAsync(cancellationToken);

        int created = 0;
        int updated = 0;
        int skipped = 0;

        foreach (ScenarioDefinitionImportRecord item in request.Definitions)
        {
            if (string.IsNullOrWhiteSpace(item.ScenarioKey) || string.IsNullOrWhiteSpace(item.Name))
            {
                skipped++;
                continue;
            }

            ScenarioDefinition? existing = await dbContext.ScenarioDefinitions
                .SingleOrDefaultAsync(
                    x => x.LabEnvironmentId == environment.Id && x.ScenarioKey == item.ScenarioKey,
                    cancellationToken);

            string definitionJson = JsonSerializer.Serialize(item.Script, JsonOptions);
            string replaySignature = ComputeReplaySignature(item.ScenarioKey, item.DeterministicSeed, definitionJson);

            if (existing is null)
            {
                dbContext.ScenarioDefinitions.Add(new ScenarioDefinition
                {
                    Id = Guid.NewGuid(),
                    LabEnvironmentId = environment.Id,
                    ScenarioKey = item.ScenarioKey.Trim(),
                    Name = item.Name.Trim(),
                    Description = item.Description.Trim(),
                    DeterministicSeed = item.DeterministicSeed,
                    DefinitionJson = definitionJson,
                    ReplaySignature = replaySignature,
                    IsActive = item.IsActive,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                });
                created++;
                continue;
            }

            if (!request.ReplaceExisting)
            {
                skipped++;
                continue;
            }

            existing.Name = item.Name.Trim();
            existing.Description = item.Description.Trim();
            existing.DeterministicSeed = item.DeterministicSeed;
            existing.DefinitionJson = definitionJson;
            existing.ReplaySignature = replaySignature;
            existing.IsActive = item.IsActive;
            existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
            updated++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        IReadOnlyList<ScenarioDefinitionView> definitions = await ListScenariosAsync(cancellationToken);

        return new ScenarioImportResult(
            request.Definitions.Count,
            updated,
            created,
            skipped,
            definitions);
    }

    public async Task<IReadOnlyList<ScenarioDefinitionImportRecord>> ExportAsync(CancellationToken cancellationToken = default)
    {
        List<ScenarioDefinition> definitions = await dbContext.ScenarioDefinitions
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        return definitions
            .Select(x => new ScenarioDefinitionImportRecord
            {
                ScenarioKey = x.ScenarioKey,
                Name = x.Name,
                Description = x.Description,
                DeterministicSeed = x.DeterministicSeed,
                IsActive = x.IsActive,
                Script = DeserializeScript(x.DefinitionJson),
            })
            .ToArray();
    }

    private async Task<ScenarioStepResultView> ExecuteActionAsync(
        int order,
        ScenarioActionDefinition action,
        ScenarioExecutionContext context,
        CancellationToken cancellationToken)
    {
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        string kind = Normalize(action.Kind);
        string name = string.IsNullOrWhiteSpace(action.Name) ? action.Kind : action.Name.Trim();
        string correlationId = ResolveCorrelationId(action, context, order);

        switch (kind)
        {
            case "preauth":
                {
                    string operation = Normalize(action.Action) switch
                    {
                        "create" => "preauth-create",
                        "authorize" => "preauth-authorize",
                        "cancel" => "preauth-cancel",
                        "expire" => "expire",
                        _ => throw new InvalidOperationException($"Unsupported preauth action '{action.Action}'."),
                    };

                    if (operation == "expire")
                    {
                        string preAuthId = await ResolveLatestPreAuthExternalIdAsync(context.Site.SiteCode, correlationId, cancellationToken);
                        PreAuthSessionSummary? expired = await preAuthSimulationService.ExpireSessionAsync(
                            new PreAuthManualExpiryRequest
                            {
                                SiteCode = context.Site.SiteCode,
                                PreAuthId = preAuthId,
                                CorrelationId = correlationId,
                            },
                            cancellationToken);

                        string outputJson = JsonSerializer.Serialize(expired, JsonOptions);
                        return new ScenarioStepResultView(
                            order,
                            action.Kind,
                            name,
                            expired is null ? "Failed" : "Succeeded",
                            correlationId,
                            expired is null ? $"Pre-auth '{preAuthId}' could not be expired." : $"Pre-auth '{preAuthId}' expired.",
                            SafeJson(outputJson),
                            startedAtUtc,
                            DateTimeOffset.UtcNow);
                    }

                    string requestJson = JsonSerializer.Serialize(
                        new
                        {
                            correlationId,
                            pump = action.PumpNumber,
                            nozzle = action.NozzleNumber,
                            amount = action.Amount,
                            expiresInSeconds = action.ExpiresInSeconds,
                            simulateFailure = action.SimulateFailure,
                            failureStatusCode = action.FailureStatusCode,
                            failureMessage = action.FailureMessage,
                            failureCode = action.FailureCode,
                            customerName = action.CustomerName,
                            customerTaxId = action.CustomerTaxId,
                            customerTaxOffice = action.CustomerTaxOffice,
                        },
                        JsonOptions);

                    PreAuthSimulationResponse response = await preAuthSimulationService.HandleAsync(
                        new PreAuthSimulationRequest(
                            context.Site.SiteCode,
                            operation,
                            HttpMethods.Post,
                            $"/api/scenarios/{context.Definition.ScenarioKey}/preauth",
                            context.Run.CorrelationId,
                            requestJson,
                            ExtractSampleValues(requestJson)),
                        cancellationToken);
                    PreAuthSessionSummary? session = await preAuthSimulationService.GetSessionAsync(
                        context.Site.SiteCode,
                        correlationId,
                        null,
                        cancellationToken);

                    context.TrackCorrelation(correlationId);

                    return new ScenarioStepResultView(
                        order,
                        action.Kind,
                        name,
                        response.StatusCode is >= 200 and < 300 ? "Succeeded" : "Failed",
                        correlationId,
                        $"Pre-auth action '{action.Action}' returned HTTP {response.StatusCode}.",
                        JsonSerializer.Serialize(
                            new
                            {
                                response.StatusCode,
                                response = DeserializeJsonObject(response.Body),
                                session,
                            },
                            JsonOptions),
                        startedAtUtc,
                        DateTimeOffset.UtcNow);
                }
            case "lift":
                {
                    (Pump pump, Nozzle nozzle) = await ResolvePumpAndNozzleAsync(context.Site.Id, action, cancellationToken);
                    NozzleActionResult result = await forecourtSimulationService.LiftAsync(
                        context.Site.Id,
                        pump.Id,
                        nozzle.Id,
                        new NozzleLiftRequest
                        {
                            CorrelationId = correlationId,
                            FaultMessage = action.FailureMessage,
                        },
                        cancellationToken);

                    context.TrackCorrelation(correlationId);

                    return new ScenarioStepResultView(
                        order,
                        action.Kind,
                        name,
                        result.StatusCode is >= 200 and < 300 ? "Succeeded" : "Failed",
                        correlationId,
                        result.Message,
                        JsonSerializer.Serialize(result, JsonOptions),
                        startedAtUtc,
                        DateTimeOffset.UtcNow);
                }
            case "dispense":
                {
                    (Pump pump, Nozzle nozzle) = await ResolvePumpAndNozzleAsync(context.Site.Id, action, cancellationToken);
                    NozzleActionResult result = await forecourtSimulationService.DispenseAsync(
                        context.Site.Id,
                        pump.Id,
                        nozzle.Id,
                        new DispenseSimulationRequest
                        {
                            Action = string.IsNullOrWhiteSpace(action.Action) ? "start" : action.Action,
                            CorrelationId = correlationId,
                            FlowRateLitresPerMinute = action.FlowRateLitresPerMinute,
                            TargetAmount = action.TargetAmount,
                            TargetVolume = action.TargetVolume,
                            ElapsedSeconds = action.ElapsedSeconds,
                            InjectDuplicate = action.InjectDuplicate,
                            SimulateFailure = action.SimulateFailure,
                            FailureMessage = action.FailureMessage,
                        },
                        cancellationToken);

                    context.TrackCorrelation(correlationId);

                    return new ScenarioStepResultView(
                        order,
                        action.Kind,
                        name,
                        result.StatusCode is >= 200 and < 300 ? "Succeeded" : "Failed",
                        correlationId,
                        result.Message,
                        JsonSerializer.Serialize(result, JsonOptions),
                        startedAtUtc,
                        DateTimeOffset.UtcNow);
                }
            case "hang":
                {
                    (Pump pump, Nozzle nozzle) = await ResolvePumpAndNozzleAsync(context.Site.Id, action, cancellationToken);
                    NozzleActionResult result = await forecourtSimulationService.HangAsync(
                        context.Site.Id,
                        pump.Id,
                        nozzle.Id,
                        new NozzleHangRequest
                        {
                            CorrelationId = correlationId,
                            ElapsedSeconds = action.ElapsedSeconds,
                            ClearFault = action.ClearFault,
                        },
                        cancellationToken);

                    context.TrackCorrelation(correlationId);
                    await CacheLatestTransactionsAsync(context, correlationId, cancellationToken);

                    return new ScenarioStepResultView(
                        order,
                        action.Kind,
                        name,
                        result.StatusCode is >= 200 and < 300 ? "Succeeded" : "Failed",
                        correlationId,
                        result.Message,
                        JsonSerializer.Serialize(result, JsonOptions),
                        startedAtUtc,
                        DateTimeOffset.UtcNow);
                }
            case "push-transactions":
                {
                    IReadOnlyList<string> transactionIds = await ResolveTransactionIdsAsync(context, action, cancellationToken);
                    PushTransactionsResult result = await forecourtSimulationService.PushTransactionsAsync(
                        context.Site.Id,
                        new PushTransactionsRequest
                        {
                            TransactionIds = transactionIds,
                            TargetKey = action.TargetKey,
                        },
                        cancellationToken);

                    return new ScenarioStepResultView(
                        order,
                        action.Kind,
                        name,
                        result.StatusCode is >= 200 and < 300 ? "Succeeded" : "Failed",
                        correlationId,
                        result.Message,
                        JsonSerializer.Serialize(result, JsonOptions),
                        startedAtUtc,
                        DateTimeOffset.UtcNow);
                }
            case "pull-transactions":
                {
                    PullTransactionsResult result = await forecourtSimulationService.PullTransactionsAsync(
                        context.Site.SiteCode,
                        action.Limit ?? 100,
                        cursor: null,
                        cancellationToken);

                    context.LastPulledTransactionIds = ExtractPulledTransactionIds(result.ResponseBody);
                    return new ScenarioStepResultView(
                        order,
                        action.Kind,
                        name,
                        result.StatusCode is >= 200 and < 300 ? "Succeeded" : "Failed",
                        correlationId,
                        $"Pulled {context.LastPulledTransactionIds.Count} transactions.",
                        SafeJson(result.ResponseBody),
                        startedAtUtc,
                        DateTimeOffset.UtcNow);
                }
            case "acknowledge-transactions":
                {
                    IReadOnlyList<string> transactionIds = context.LastPulledTransactionIds.Count > 0
                        ? context.LastPulledTransactionIds
                        : await ResolveTransactionIdsAsync(context, action, cancellationToken);
                    AcknowledgeTransactionsResult result = await forecourtSimulationService.AcknowledgeTransactionsAsync(
                        context.Site.SiteCode,
                        new AcknowledgeTransactionsRequest
                        {
                            CorrelationId = correlationId,
                            TransactionIds = transactionIds,
                        },
                        cancellationToken);

                    context.TrackCorrelation(correlationId);

                    return new ScenarioStepResultView(
                        order,
                        action.Kind,
                        name,
                        result.StatusCode is >= 200 and < 300 ? "Succeeded" : "Failed",
                        correlationId,
                        $"Acknowledged {transactionIds.Count} transactions.",
                        SafeJson(result.ResponseBody),
                        startedAtUtc,
                        DateTimeOffset.UtcNow);
                }
            case "delay":
                await Task.Delay(Math.Max(action.DelayMs ?? 0, 0), cancellationToken);
                return new ScenarioStepResultView(
                    order,
                    action.Kind,
                    name,
                    "Succeeded",
                    correlationId,
                    $"Delayed for {Math.Max(action.DelayMs ?? 0, 0)} ms.",
                    JsonSerializer.Serialize(new { delayMs = Math.Max(action.DelayMs ?? 0, 0) }, JsonOptions),
                    startedAtUtc,
                    DateTimeOffset.UtcNow);
            case "callback-replay":
                {
                    IReadOnlyList<CallbackHistoryItemView> history = await callbackCaptureService.ListHistoryAsync(
                        action.TargetKey ?? string.Empty,
                        20,
                        cancellationToken);
                    CallbackHistoryItemView? latest = history.FirstOrDefault(x =>
                        string.IsNullOrWhiteSpace(action.CorrelationAlias) ||
                        x.CorrelationId == context.GetCorrelation(action.CorrelationAlias));

                    if (latest is null)
                    {
                        return new ScenarioStepResultView(
                            order,
                            action.Kind,
                            name,
                            "Failed",
                            correlationId,
                            $"No callback capture history was found for target '{action.TargetKey}'.",
                            "{}",
                            startedAtUtc,
                            DateTimeOffset.UtcNow);
                    }

                    CallbackReplayResult? replay = await callbackCaptureService.ReplayAsync(
                        action.TargetKey ?? string.Empty,
                        latest.Id,
                        cancellationToken);

                    return new ScenarioStepResultView(
                        order,
                        action.Kind,
                        name,
                        replay is null ? "Failed" : "Succeeded",
                        correlationId,
                        replay?.Message ?? "Callback replay failed.",
                        JsonSerializer.Serialize(replay, JsonOptions),
                        startedAtUtc,
                        DateTimeOffset.UtcNow);
                }
            default:
                throw new InvalidOperationException($"Unsupported scenario action kind '{action.Kind}'.");
        }
    }

    private async Task<ScenarioAssertionResultView> EvaluateAssertionAsync(
        int order,
        ScenarioAssertionDefinition assertion,
        ScenarioExecutionContext context,
        CancellationToken cancellationToken)
    {
        string kind = Normalize(assertion.Kind);
        string name = string.IsNullOrWhiteSpace(assertion.Name) ? assertion.Kind : assertion.Name.Trim();
        string? correlationId = !string.IsNullOrWhiteSpace(assertion.CorrelationAlias)
            ? context.GetCorrelation(assertion.CorrelationAlias)
            : null;

        return kind switch
        {
            "transaction-status" => await EvaluateTransactionStatusAssertionAsync(order, name, assertion, context.Site.Id, correlationId, cancellationToken),
            "preauth-status" => await EvaluatePreAuthStatusAssertionAsync(order, name, assertion, context.Site.Id, correlationId, cancellationToken),
            "callback-attempt-count" => await EvaluateCallbackAttemptAssertionAsync(order, name, assertion, context.Site.Id, correlationId, cancellationToken),
            "callback-history-count" => await EvaluateCallbackHistoryAssertionAsync(order, name, assertion, correlationId, cancellationToken),
            "log-count" => await EvaluateLogAssertionAsync(order, name, assertion, context.Site.Id, correlationId, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported scenario assertion kind '{assertion.Kind}'."),
        };
    }

    private async Task<ScenarioAssertionResultView> EvaluateTransactionStatusAssertionAsync(
        int order,
        string name,
        ScenarioAssertionDefinition assertion,
        Guid siteId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        IQueryable<SimulatedTransaction> query = dbContext.SimulatedTransactions.AsNoTracking().Where(x => x.SiteId == siteId);
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }

        List<SimulatedTransaction> matches = await query.ToListAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(assertion.ExpectedStatus) &&
            Enum.TryParse(assertion.ExpectedStatus, ignoreCase: true, out SimulatedTransactionStatus status))
        {
            matches = matches.Where(x => x.Status == status).ToList();
        }

        int actual = matches.Count;
        int minimum = assertion.MinimumCount ?? assertion.ExpectedCount ?? 1;
        bool passed = assertion.ExpectedCount.HasValue ? actual == assertion.ExpectedCount.Value : actual >= minimum;

        return new ScenarioAssertionResultView(
            order,
            assertion.Kind,
            name,
            passed,
            $"Expected {(assertion.ExpectedCount.HasValue ? assertion.ExpectedCount.Value : minimum)} transaction(s); observed {actual}.",
            JsonSerializer.Serialize(
                new
                {
                    correlationId,
                    assertion.ExpectedStatus,
                    actualCount = actual,
                    transactions = matches.Select(x => new { x.ExternalTransactionId, x.Status, x.CorrelationId }),
                },
                JsonOptions));
    }

    private async Task<ScenarioAssertionResultView> EvaluatePreAuthStatusAssertionAsync(
        int order,
        string name,
        ScenarioAssertionDefinition assertion,
        Guid siteId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        IQueryable<PreAuthSession> query = dbContext.PreAuthSessions.AsNoTracking().Where(x => x.SiteId == siteId);
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }

        List<PreAuthSession> matches = await query.ToListAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(assertion.ExpectedStatus) &&
            Enum.TryParse(assertion.ExpectedStatus, ignoreCase: true, out PreAuthSessionStatus status))
        {
            matches = matches.Where(x => x.Status == status).ToList();
        }

        int actual = matches.Count;
        int minimum = assertion.MinimumCount ?? assertion.ExpectedCount ?? 1;
        bool passed = assertion.ExpectedCount.HasValue ? actual == assertion.ExpectedCount.Value : actual >= minimum;

        return new ScenarioAssertionResultView(
            order,
            assertion.Kind,
            name,
            passed,
            $"Expected {(assertion.ExpectedCount.HasValue ? assertion.ExpectedCount.Value : minimum)} pre-auth session(s); observed {actual}.",
            JsonSerializer.Serialize(
                new
                {
                    correlationId,
                    assertion.ExpectedStatus,
                    actualCount = actual,
                    sessions = matches.Select(x => new { x.ExternalReference, x.Status, x.CorrelationId }),
                },
                JsonOptions));
    }

    private async Task<ScenarioAssertionResultView> EvaluateCallbackAttemptAssertionAsync(
        int order,
        string name,
        ScenarioAssertionDefinition assertion,
        Guid siteId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        IQueryable<CallbackAttempt> query = dbContext.CallbackAttempts
            .AsNoTracking()
            .Where(x => x.SimulatedTransaction.SiteId == siteId);

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }

        if (!string.IsNullOrWhiteSpace(assertion.TargetKey))
        {
            query = query.Where(x => x.CallbackTarget.TargetKey == assertion.TargetKey);
        }

        List<CallbackAttempt> attempts = await query.ToListAsync(cancellationToken);
        int actual = attempts.Count;
        int minimum = assertion.MinimumCount ?? assertion.ExpectedCount ?? 1;
        bool passed = assertion.ExpectedCount.HasValue ? actual == assertion.ExpectedCount.Value : actual >= minimum;

        return new ScenarioAssertionResultView(
            order,
            assertion.Kind,
            name,
            passed,
            $"Expected {(assertion.ExpectedCount.HasValue ? assertion.ExpectedCount.Value : minimum)} callback attempt(s); observed {actual}.",
            JsonSerializer.Serialize(
                new
                {
                    correlationId,
                    assertion.TargetKey,
                    actualCount = actual,
                    attempts = attempts.Select(x => new { x.AttemptNumber, x.Status, x.ResponseStatusCode }),
                },
                JsonOptions));
    }

    private async Task<ScenarioAssertionResultView> EvaluateCallbackHistoryAssertionAsync(
        int order,
        string name,
        ScenarioAssertionDefinition assertion,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CallbackHistoryItemView> history = await callbackCaptureService.ListHistoryAsync(
            assertion.TargetKey ?? string.Empty,
            200,
            cancellationToken);

        IReadOnlyList<CallbackHistoryItemView> filtered = history
            .Where(x =>
                (string.IsNullOrWhiteSpace(correlationId) || x.CorrelationId == correlationId) &&
                (!assertion.IsReplay.HasValue || x.IsReplay == assertion.IsReplay.Value))
            .ToArray();

        int actual = filtered.Count;
        int minimum = assertion.MinimumCount ?? assertion.ExpectedCount ?? 1;
        bool passed = assertion.ExpectedCount.HasValue ? actual == assertion.ExpectedCount.Value : actual >= minimum;

        return new ScenarioAssertionResultView(
            order,
            assertion.Kind,
            name,
            passed,
            $"Expected {(assertion.ExpectedCount.HasValue ? assertion.ExpectedCount.Value : minimum)} callback history record(s); observed {actual}.",
            JsonSerializer.Serialize(
                new
                {
                    correlationId,
                    assertion.TargetKey,
                    assertion.IsReplay,
                    actualCount = actual,
                    captures = filtered.Select(x => new { x.Id, x.AuthOutcome, x.IsReplay, x.ResponseStatusCode }),
                },
                JsonOptions));
    }

    private async Task<ScenarioAssertionResultView> EvaluateLogAssertionAsync(
        int order,
        string name,
        ScenarioAssertionDefinition assertion,
        Guid siteId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        IQueryable<LabEventLog> query = dbContext.LabEventLogs.AsNoTracking().Where(x => x.SiteId == siteId);
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            query = query.Where(x => x.CorrelationId == correlationId);
        }

        if (!string.IsNullOrWhiteSpace(assertion.Category))
        {
            query = query.Where(x => x.Category == assertion.Category);
        }

        if (!string.IsNullOrWhiteSpace(assertion.EventType))
        {
            query = query.Where(x => x.EventType == assertion.EventType);
        }

        List<LabEventLog> logs = await query.ToListAsync(cancellationToken);
        int actual = logs.Count;
        int minimum = assertion.MinimumCount ?? assertion.ExpectedCount ?? 1;
        bool passed = assertion.ExpectedCount.HasValue ? actual == assertion.ExpectedCount.Value : actual >= minimum;

        return new ScenarioAssertionResultView(
            order,
            assertion.Kind,
            name,
            passed,
            $"Expected {(assertion.ExpectedCount.HasValue ? assertion.ExpectedCount.Value : minimum)} log record(s); observed {actual}.",
            JsonSerializer.Serialize(
                new
                {
                    correlationId,
                    assertion.Category,
                    assertion.EventType,
                    actualCount = actual,
                },
                JsonOptions));
    }

    private async Task ApplySetupAsync(Site site, ScenarioSetupDefinition setup, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(setup.ProfileKey))
        {
            FccSimulatorProfile profile = await dbContext.FccSimulatorProfiles
                .SingleAsync(x => x.ProfileKey == setup.ProfileKey && x.IsActive, cancellationToken);
            site.ActiveFccSimulatorProfileId = profile.Id;
        }

        if (setup.DeliveryMode.HasValue)
        {
            site.DeliveryMode = setup.DeliveryMode.Value;
        }

        if (setup.PreAuthMode.HasValue)
        {
            site.PreAuthMode = setup.PreAuthMode.Value;
        }

        if (setup.ResetNozzles)
        {
            foreach (Nozzle nozzle in site.Pumps.SelectMany(x => x.Nozzles))
            {
                nozzle.State = NozzleState.Idle;
                nozzle.SimulationStateJson = "{}";
                nozzle.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }
        }

        if (setup.ClearActivePreAuth)
        {
            List<PreAuthSession> activeSessions = await dbContext.PreAuthSessions
                .Where(x =>
                    x.SiteId == site.Id &&
                    (x.Status == PreAuthSessionStatus.Pending ||
                     x.Status == PreAuthSessionStatus.Authorized ||
                     x.Status == PreAuthSessionStatus.Dispensing))
                .ToListAsync(cancellationToken);

            foreach (PreAuthSession session in activeSessions)
            {
                session.Status = PreAuthSessionStatus.Cancelled;
                session.CompletedAtUtc ??= DateTimeOffset.UtcNow;
            }
        }

        site.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task RestoreSetupAsync(Site site, SiteSnapshot snapshot, CancellationToken cancellationToken)
    {
        site.ActiveFccSimulatorProfileId = snapshot.ActiveProfileId;
        site.DeliveryMode = snapshot.DeliveryMode;
        site.PreAuthMode = snapshot.PreAuthMode;
        site.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task AttachArtifactsAsync(
        Guid siteId,
        Guid scenarioRunId,
        IReadOnlyCollection<string> correlationIds,
        CancellationToken cancellationToken)
    {
        if (correlationIds.Count == 0)
        {
            return;
        }

        List<PreAuthSession> sessions = await dbContext.PreAuthSessions
            .Where(x => x.SiteId == siteId && x.ScenarioRunId == null && correlationIds.Contains(x.CorrelationId))
            .ToListAsync(cancellationToken);
        foreach (PreAuthSession session in sessions)
        {
            session.ScenarioRunId = scenarioRunId;
        }

        List<SimulatedTransaction> transactions = await dbContext.SimulatedTransactions
            .Where(x => x.SiteId == siteId && x.ScenarioRunId == null && correlationIds.Contains(x.CorrelationId))
            .ToListAsync(cancellationToken);
        foreach (SimulatedTransaction transaction in transactions)
        {
            transaction.ScenarioRunId = scenarioRunId;
        }

        List<LabEventLog> logs = await dbContext.LabEventLogs
            .Where(x => x.SiteId == siteId && x.ScenarioRunId == null && correlationIds.Contains(x.CorrelationId))
            .ToListAsync(cancellationToken);
        foreach (LabEventLog log in logs)
        {
            log.ScenarioRunId = scenarioRunId;
        }

        if (sessions.Count > 0 || transactions.Count > 0 || logs.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<(Pump Pump, Nozzle Nozzle)> ResolvePumpAndNozzleAsync(
        Guid siteId,
        ScenarioActionDefinition action,
        CancellationToken cancellationToken)
    {
        int pumpNumber = action.PumpNumber ?? 1;
        int nozzleNumber = action.NozzleNumber ?? 1;

        Nozzle? nozzle = await dbContext.Nozzles
            .Include(x => x.Pump)
            .SingleOrDefaultAsync(
                x => x.Pump.SiteId == siteId &&
                     x.Pump.PumpNumber == pumpNumber &&
                     x.NozzleNumber == nozzleNumber,
                cancellationToken);

        if (nozzle is null)
        {
            throw new InvalidOperationException($"Pump {pumpNumber} / nozzle {nozzleNumber} could not be resolved.");
        }

        return (nozzle.Pump, nozzle);
    }

    private async Task CacheLatestTransactionsAsync(
        ScenarioExecutionContext context,
        string correlationId,
        CancellationToken cancellationToken)
    {
        List<string> ids = await dbContext.SimulatedTransactions
            .AsNoTracking()
            .Where(x => x.SiteId == context.Site.Id && x.CorrelationId == correlationId)
            .OrderByDescending(x => x.OccurredAtUtc)
            .Select(x => x.ExternalTransactionId)
            .ToListAsync(cancellationToken);

        if (ids.Count > 0)
        {
            context.LastTransactionIds = ids;
        }
    }

    private async Task<IReadOnlyList<string>> ResolveTransactionIdsAsync(
        ScenarioExecutionContext context,
        ScenarioActionDefinition action,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(action.CorrelationAlias))
        {
            string? correlationId = context.GetCorrelation(action.CorrelationAlias);
            if (!string.IsNullOrWhiteSpace(correlationId))
            {
                List<string> ids = await dbContext.SimulatedTransactions
                    .AsNoTracking()
                    .Where(x => x.SiteId == context.Site.Id && x.CorrelationId == correlationId)
                    .OrderBy(x => x.OccurredAtUtc)
                    .Select(x => x.ExternalTransactionId)
                    .ToListAsync(cancellationToken);
                if (ids.Count > 0)
                {
                    return ids;
                }
            }
        }

        return context.LastTransactionIds;
    }

    private async Task<string> ResolveLatestPreAuthExternalIdAsync(
        string siteCode,
        string correlationId,
        CancellationToken cancellationToken)
    {
        PreAuthSessionSummary? session = await preAuthSimulationService.GetSessionAsync(siteCode, correlationId, null, cancellationToken);
        return session?.ExternalReference
            ?? throw new InvalidOperationException($"No pre-auth session was found for correlation '{correlationId}'.");
    }

    private static IReadOnlyList<string> ExtractPulledTransactionIds(string responseBody)
    {
        List<string> ids = [];

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return ids;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("transactions", out JsonElement transactions) ||
                transactions.ValueKind != JsonValueKind.Array)
            {
                return ids;
            }

            foreach (JsonElement transaction in transactions.EnumerateArray())
            {
                if (transaction.ValueKind == JsonValueKind.Object &&
                    transaction.TryGetProperty("transactionId", out JsonElement transactionId) &&
                    transactionId.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(transactionId.GetString()))
                {
                    ids.Add(transactionId.GetString()!);
                }
            }
        }
        catch (JsonException)
        {
        }

        return ids;
    }

    private static string ResolveCorrelationId(
        ScenarioActionDefinition action,
        ScenarioExecutionContext context,
        int order)
    {
        if (!string.IsNullOrWhiteSpace(action.CorrelationId))
        {
            context.TrackCorrelation(action.CorrelationId.Trim(), action.CorrelationAlias);
            return action.CorrelationId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(action.CorrelationAlias))
        {
            string existing = context.GetOrCreateCorrelation(action.CorrelationAlias);
            return existing;
        }

        string generated = $"{context.Run.CorrelationId}-step-{order:D2}";
        context.TrackCorrelation(generated);
        return generated.Length <= 64 ? generated : generated[..64];
    }

    private static string BuildRunCorrelationId(string scenarioKey, int replaySeed)
    {
        string key = scenarioKey.Replace('_', '-').Replace(' ', '-').ToLowerInvariant();
        string value = $"scenario-{key}-{replaySeed}";
        return value.Length <= 64 ? value : value[..64];
    }

    private static string ComputeReplaySignature(string scenarioKey, int replaySeed, string definitionJson)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes($"{scenarioKey}|{replaySeed}|{definitionJson}");
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()[..32];
    }

    private void AddScenarioLog(
        ScenarioRun run,
        Site site,
        string severity,
        string eventType,
        string message,
        string correlationId,
        object? metadata)
    {
        dbContext.LabEventLogs.Add(new LabEventLog
        {
            Id = Guid.NewGuid(),
            SiteId = site.Id,
            FccSimulatorProfileId = site.ActiveFccSimulatorProfileId,
            ScenarioRunId = run.Id,
            CorrelationId = correlationId,
            Severity = severity,
            Category = "ScenarioRun",
            EventType = eventType,
            Message = message,
            RawPayloadJson = "{}",
            CanonicalPayloadJson = "{}",
            MetadataJson = JsonSerializer.Serialize(metadata ?? new { }, JsonOptions),
            OccurredAtUtc = DateTimeOffset.UtcNow,
        });
    }

    private async Task<ScenarioDefinition> ResolveDefinitionAsync(ScenarioRunRequest request, CancellationToken cancellationToken)
    {
        if (request.ScenarioId.HasValue)
        {
            return await dbContext.ScenarioDefinitions.SingleAsync(x => x.Id == request.ScenarioId.Value, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(request.ScenarioKey))
        {
            return await dbContext.ScenarioDefinitions.SingleAsync(x => x.ScenarioKey == request.ScenarioKey, cancellationToken);
        }

        throw new InvalidOperationException("A scenario id or scenario key is required.");
    }

    private static ScenarioScriptDefinition DeserializeScript(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ScenarioScriptDefinition>(json, JsonOptions) ?? new ScenarioScriptDefinition();
        }
        catch (JsonException)
        {
            return new ScenarioScriptDefinition();
        }
    }

    private static ScenarioDefinitionView MapDefinition(ScenarioDefinition definition, ScenarioRun? latestRun)
    {
        return new ScenarioDefinitionView(
            definition.Id,
            definition.LabEnvironmentId,
            definition.ScenarioKey,
            definition.Name,
            definition.Description,
            definition.DeterministicSeed,
            definition.ReplaySignature,
            definition.IsActive,
            DeserializeScript(definition.DefinitionJson),
            latestRun is null ? null : MapRunSummary(latestRun));
    }

    private static ScenarioRunSummaryView MapRunSummary(ScenarioRun run)
    {
        using JsonDocument summary = JsonDocument.Parse(string.IsNullOrWhiteSpace(run.ResultSummaryJson) ? "{}" : run.ResultSummaryJson);
        int stepCount = summary.RootElement.TryGetProperty("steps", out JsonElement steps) && steps.ValueKind == JsonValueKind.Array
            ? steps.GetArrayLength()
            : 0;
        int assertionCount = summary.RootElement.TryGetProperty("assertions", out JsonElement assertions) && assertions.ValueKind == JsonValueKind.Array
            ? assertions.GetArrayLength()
            : 0;
        int errorCount = summary.RootElement.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Array
            ? errors.GetArrayLength()
            : 0;

        return new ScenarioRunSummaryView(
            run.Id,
            run.SiteId,
            run.ScenarioDefinitionId,
            run.ScenarioDefinition.ScenarioKey,
            run.ScenarioDefinition.Name,
            run.Site.SiteCode,
            run.CorrelationId,
            run.ReplaySeed,
            run.ReplaySignature,
            run.Status,
            run.StartedAtUtc,
            run.CompletedAtUtc,
            stepCount,
            assertionCount,
            errorCount);
    }

    private static ScenarioRunDetailView MapRunDetail(ScenarioRun run)
    {
        using JsonDocument summary = JsonDocument.Parse(string.IsNullOrWhiteSpace(run.ResultSummaryJson) ? "{}" : run.ResultSummaryJson);
        IReadOnlyList<ScenarioStepResultView> steps = summary.RootElement.TryGetProperty("steps", out JsonElement stepsElement)
            ? JsonSerializer.Deserialize<IReadOnlyList<ScenarioStepResultView>>(stepsElement.GetRawText(), JsonOptions) ?? []
            : [];
        IReadOnlyList<ScenarioAssertionResultView> assertions = summary.RootElement.TryGetProperty("assertions", out JsonElement assertionsElement)
            ? JsonSerializer.Deserialize<IReadOnlyList<ScenarioAssertionResultView>>(assertionsElement.GetRawText(), JsonOptions) ?? []
            : [];

        return new ScenarioRunDetailView(
            run.Id,
            run.SiteId,
            run.ScenarioDefinitionId,
            run.ScenarioDefinition.ScenarioKey,
            run.ScenarioDefinition.Name,
            run.Site.SiteCode,
            run.CorrelationId,
            run.ReplaySeed,
            run.ReplaySignature,
            run.Status,
            SafeJson(run.InputSnapshotJson),
            SafeJson(run.ResultSummaryJson),
            run.StartedAtUtc,
            run.CompletedAtUtc,
            steps,
            assertions);
    }

    private static Dictionary<string, string> ExtractSampleValues(string body)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(body))
        {
            return values;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return values;
            }

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                values[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                    JsonValueKind.Number => property.Value.ToString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => string.Empty,
                    _ => property.Value.GetRawText(),
                };
            }
        }
        catch (JsonException)
        {
        }

        return values;
    }

    private static object? DeserializeJsonObject(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<object>(string.IsNullOrWhiteSpace(json) ? "{}" : json, JsonOptions);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string SafeJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        try
        {
            using JsonDocument _ = JsonDocument.Parse(json);
            return json;
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(json, JsonOptions);
        }
    }

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
    }

    private sealed record SiteSnapshot(
        Guid ActiveProfileId,
        TransactionDeliveryMode DeliveryMode,
        PreAuthFlowMode PreAuthMode);

    private sealed class ScenarioExecutionContext(
        ScenarioRun run,
        ScenarioDefinition definition,
        ScenarioScriptDefinition script,
        Site site,
        int replaySeed)
    {
        public ScenarioRun Run { get; } = run;
        public ScenarioDefinition Definition { get; } = definition;
        public ScenarioScriptDefinition Script { get; } = script;
        public Site Site { get; } = site;
        public int ReplaySeed { get; } = replaySeed;
        public List<ScenarioStepResultView> Steps { get; } = [];
        public List<ScenarioAssertionResultView> Assertions { get; } = [];
        public List<string> Errors { get; } = [];
        public IReadOnlyList<string> LastPulledTransactionIds { get; set; } = [];
        public IReadOnlyList<string> LastTransactionIds { get; set; } = [];
        public HashSet<string> KnownCorrelationIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> CorrelationsByAlias { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void TrackCorrelation(string correlationId, string? alias = null)
        {
            if (!string.IsNullOrWhiteSpace(correlationId))
            {
                KnownCorrelationIds.Add(correlationId);
            }

            if (!string.IsNullOrWhiteSpace(alias) && !string.IsNullOrWhiteSpace(correlationId))
            {
                CorrelationsByAlias[alias] = correlationId;
            }
        }

        public string GetOrCreateCorrelation(string alias)
        {
            if (CorrelationsByAlias.TryGetValue(alias, out string? existing))
            {
                return existing;
            }

            string value = $"{Run.CorrelationId}-{alias}".ToLowerInvariant();
            value = value.Length <= 64 ? value : value[..64];
            CorrelationsByAlias[alias] = value;
            KnownCorrelationIds.Add(value);
            return value;
        }

        public string? GetCorrelation(string alias)
        {
            return CorrelationsByAlias.GetValueOrDefault(alias);
        }
    }

    private sealed class ScenarioRunFailedException(string message) : Exception(message);
}
