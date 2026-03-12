using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using VirtualLab.Application.ContractValidation;
using VirtualLab.Application.Forecourt;
using VirtualLab.Application.Observability;
using VirtualLab.Domain.Enums;
using VirtualLab.Domain.Models;
using VirtualLab.Domain.Profiles;
using VirtualLab.Infrastructure.FccProfiles;
using VirtualLab.Infrastructure.Persistence;

namespace VirtualLab.Infrastructure.Observability;

public sealed class ObservabilityService(
    VirtualLabDbContext dbContext,
    IForecourtSimulationService forecourtSimulationService,
    IContractValidationService contractValidationService) : IObservabilityService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<IReadOnlyList<TransactionListItemView>> ListTransactionsAsync(
        TransactionListQuery query,
        CancellationToken cancellationToken = default)
    {
        int take = Math.Clamp(query.Limit, 1, 100);
        string? search = Normalize(query.Search);
        string? correlationId = Normalize(query.CorrelationId);
        string? siteCode = Normalize(query.SiteCode);

        IQueryable<SimulatedTransaction> transactionQuery = dbContext.SimulatedTransactions
            .AsNoTracking();

        if (query.SiteId.HasValue)
        {
            transactionQuery = transactionQuery.Where(x => x.SiteId == query.SiteId.Value);
        }
        else if (!string.IsNullOrWhiteSpace(siteCode))
        {
            transactionQuery = transactionQuery.Where(x => x.Site.SiteCode == siteCode);
        }

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            transactionQuery = transactionQuery.Where(x => x.CorrelationId == correlationId);
        }

        if (query.DeliveryMode.HasValue)
        {
            transactionQuery = transactionQuery.Where(x => x.DeliveryMode == query.DeliveryMode.Value);
        }

        if (query.Status.HasValue)
        {
            transactionQuery = transactionQuery.Where(x => x.Status == query.Status.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            transactionQuery = transactionQuery.Where(x =>
                x.ExternalTransactionId.Contains(search) ||
                x.CorrelationId.Contains(search) ||
                x.Product.ProductCode.Contains(search));
        }

        var rows = await transactionQuery
            .OrderByDescending(x => x.OccurredAtUtc)
            .Take(take)
            .Select(x => new
            {
                x.Id,
                x.SiteId,
                SiteCode = x.Site.SiteCode,
                SiteName = x.Site.Name,
                ProfileId = x.Site.ActiveFccSimulatorProfileId,
                ProfileKey = x.Site.ActiveFccSimulatorProfile.ProfileKey,
                x.ExternalTransactionId,
                x.CorrelationId,
                PumpNumber = x.Pump.PumpNumber,
                NozzleNumber = x.Nozzle.NozzleNumber,
                ProductCode = x.Product.ProductCode,
                ProductName = x.Product.Name,
                x.DeliveryMode,
                x.Status,
                x.Volume,
                x.TotalAmount,
                x.OccurredAtUtc,
                x.DeliveredAtUtc,
                x.PreAuthSessionId,
                x.RawPayloadJson,
                x.CanonicalPayloadJson,
                ValidationRulesJson = x.Site.ActiveFccSimulatorProfile.ValidationRulesJson,
                FieldMappingsJson = x.Site.ActiveFccSimulatorProfile.FieldMappingsJson,
                x.MetadataJson,
                x.TimelineJson,
                CallbackAttemptCount = x.CallbackAttempts.Count(),
                LastCallbackStatus = x.CallbackAttempts
                    .OrderByDescending(y => y.AttemptedAtUtc)
                    .Select(y => (CallbackAttemptStatus?)y.Status)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(x => new TransactionListItemView(
                x.Id,
                x.SiteId,
                x.SiteCode,
                x.SiteName,
                x.ProfileId,
                x.ProfileKey,
                x.ExternalTransactionId,
                x.CorrelationId,
                x.PumpNumber,
                x.NozzleNumber,
                x.ProductCode,
                x.ProductName,
                x.DeliveryMode,
                x.Status,
                x.Volume,
                x.TotalAmount,
                x.OccurredAtUtc,
                x.DeliveredAtUtc,
                x.PreAuthSessionId,
                x.CallbackAttemptCount,
                x.LastCallbackStatus?.ToString(),
                SafeJson(x.RawPayloadJson),
                SafeJson(x.CanonicalPayloadJson),
                contractValidationService.Validate(
                    BuildValidationContract(x.ValidationRulesJson, x.FieldMappingsJson),
                    ContractValidationScopes.Transaction,
                    SafeJson(x.RawPayloadJson),
                    SafeJson(x.CanonicalPayloadJson)),
                SafeJson(x.MetadataJson),
                SafeJson(x.TimelineJson)))
            .ToArray();
    }

    public async Task<TransactionDetailView?> GetTransactionAsync(Guid transactionId, CancellationToken cancellationToken = default)
    {
        SimulatedTransaction? transaction = await dbContext.SimulatedTransactions
            .AsNoTracking()
            .Include(x => x.Site)
                .ThenInclude(x => x.ActiveFccSimulatorProfile)
            .Include(x => x.Pump)
            .Include(x => x.Nozzle)
            .Include(x => x.Product)
            .Include(x => x.CallbackAttempts)
                .ThenInclude(x => x.CallbackTarget)
            .SingleOrDefaultAsync(x => x.Id == transactionId, cancellationToken);

        if (transaction is null)
        {
            return null;
        }

        List<LabEventLog> logs = await dbContext.LabEventLogs
            .AsNoTracking()
            .Where(x =>
                x.SimulatedTransactionId == transaction.Id ||
                (transaction.PreAuthSessionId.HasValue && x.PreAuthSessionId == transaction.PreAuthSessionId.Value))
            .OrderBy(x => x.OccurredAtUtc)
            .ToListAsync(cancellationToken);

        IReadOnlyList<TransactionCallbackAttemptView> attempts = transaction.CallbackAttempts
            .OrderByDescending(x => x.AttemptedAtUtc)
            .ThenByDescending(x => x.AttemptNumber)
            .Select(MapCallbackAttempt)
            .ToArray();

        IReadOnlyList<TransactionTimelineEntryView> timeline = BuildTimeline(transaction, logs, transaction.CallbackAttempts);

        return new TransactionDetailView(
            transaction.Id,
            transaction.SiteId,
            transaction.Site.SiteCode,
            transaction.Site.Name,
            transaction.Site.ActiveFccSimulatorProfileId,
            transaction.Site.ActiveFccSimulatorProfile.ProfileKey,
            transaction.Site.ActiveFccSimulatorProfile.Name,
            transaction.ExternalTransactionId,
            transaction.CorrelationId,
            transaction.Pump.PumpNumber,
            transaction.Nozzle.NozzleNumber,
            transaction.Product.ProductCode,
            transaction.Product.Name,
            transaction.UnitPrice,
            transaction.Site.CurrencyCode,
            transaction.DeliveryMode,
            transaction.Status,
            transaction.Volume,
            transaction.TotalAmount,
            transaction.OccurredAtUtc,
            transaction.CreatedAtUtc,
            transaction.DeliveredAtUtc,
            transaction.PreAuthSessionId,
            SafeJson(transaction.RawHeadersJson),
            SafeJson(transaction.RawPayloadJson),
            SafeJson(transaction.CanonicalPayloadJson),
            contractValidationService.Validate(
                FccProfileService.ToRecord(transaction.Site.ActiveFccSimulatorProfile).Contract,
                ContractValidationScopes.Transaction,
                SafeJson(transaction.RawPayloadJson),
                SafeJson(transaction.CanonicalPayloadJson)),
            SafeJson(transaction.MetadataJson),
            attempts,
            timeline);
    }

    public async Task<TransactionReplayResult?> ReplayTransactionAsync(
        Guid transactionId,
        TransactionReplayRequest request,
        CancellationToken cancellationToken = default)
    {
        SimulatedTransaction? transaction = await dbContext.SimulatedTransactions
            .Include(x => x.Site)
                .ThenInclude(x => x.ActiveFccSimulatorProfile)
            .Include(x => x.Pump)
            .Include(x => x.Nozzle)
            .Include(x => x.Product)
            .SingleOrDefaultAsync(x => x.Id == transactionId, cancellationToken);

        if (transaction is null)
        {
            return null;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string prefix = $"{transaction.ExternalTransactionId}-R";
        int replayOrdinal = await dbContext.SimulatedTransactions
            .CountAsync(x => x.ExternalTransactionId.StartsWith(prefix), cancellationToken) + 1;

        string externalTransactionId = $"{prefix}{replayOrdinal:D2}";
        string correlationId = BuildReplayCorrelationId(transaction, request.CorrelationId, replayOrdinal);
        string occurredAtUtc = now.ToString("O");

        SimulatedTransaction replay = new()
        {
            Id = Guid.NewGuid(),
            SiteId = transaction.SiteId,
            PumpId = transaction.PumpId,
            NozzleId = transaction.NozzleId,
            ProductId = transaction.ProductId,
            PreAuthSessionId = transaction.PreAuthSessionId,
            ScenarioRunId = transaction.ScenarioRunId,
            CorrelationId = correlationId,
            ExternalTransactionId = externalTransactionId,
            DeliveryMode = transaction.DeliveryMode,
            Status = SimulatedTransactionStatus.ReadyForDelivery,
            Volume = transaction.Volume,
            UnitPrice = transaction.UnitPrice,
            TotalAmount = transaction.TotalAmount,
            OccurredAtUtc = now,
            CreatedAtUtc = now,
            DeliveredAtUtc = null,
            RawPayloadJson = PatchJson(
                transaction.RawPayloadJson,
                new Dictionary<string, object?>
                {
                    ["transactionId"] = externalTransactionId,
                    ["correlationId"] = correlationId,
                    ["occurredAtUtc"] = occurredAtUtc,
                }),
            CanonicalPayloadJson = PatchJson(
                transaction.CanonicalPayloadJson,
                new Dictionary<string, object?>
                {
                    ["fccTransactionId"] = externalTransactionId,
                    ["transactionId"] = externalTransactionId,
                    ["correlationId"] = correlationId,
                    ["occurredAtUtc"] = occurredAtUtc,
                }),
            RawHeadersJson = transaction.RawHeadersJson,
            DeliveryCursor = $"{now.UtcTicks:D20}:{externalTransactionId}",
            MetadataJson = PatchJson(
                transaction.MetadataJson,
                new Dictionary<string, object?>
                {
                    ["duplicateInjectionEnabled"] = false,
                    ["simulateFailureEnabled"] = false,
                    ["pushDeliveryCount"] = 0,
                    ["pullDeliveryCount"] = 0,
                    ["pullDuplicateEmissionCount"] = 0,
                    ["pushDuplicateEmissionCount"] = 0,
                    ["pushFailureCount"] = 0,
                    ["replayedFromTransactionId"] = transaction.Id,
                    ["replayedFromExternalTransactionId"] = transaction.ExternalTransactionId,
                    ["replayedAtUtc"] = occurredAtUtc,
                    ["originalCorrelationId"] = transaction.CorrelationId,
                }),
            TimelineJson = JsonSerializer.Serialize(
                new[]
                {
                    new
                    {
                        eventType = "TransactionReplayCreated",
                        state = SimulatedTransactionStatus.ReadyForDelivery.ToString(),
                        message = $"Manual replay created from transaction '{transaction.ExternalTransactionId}'.",
                        occurredAtUtc = now,
                        metadata = new
                        {
                            sourceTransactionId = transaction.Id,
                            sourceExternalTransactionId = transaction.ExternalTransactionId,
                            originalCorrelationId = transaction.CorrelationId,
                        },
                    },
                },
                JsonOptions),
        };

        dbContext.SimulatedTransactions.Add(replay);
        dbContext.LabEventLogs.Add(new LabEventLog
        {
            Id = Guid.NewGuid(),
            SiteId = transaction.SiteId,
            FccSimulatorProfileId = transaction.Site.ActiveFccSimulatorProfileId,
            PreAuthSessionId = replay.PreAuthSessionId,
            SimulatedTransactionId = replay.Id,
            CorrelationId = replay.CorrelationId,
            Severity = "Information",
            Category = "TransactionGenerated",
            EventType = "TransactionReplayCreated",
            Message = $"Manual replay created from transaction '{transaction.ExternalTransactionId}'.",
            RawPayloadJson = replay.RawPayloadJson,
            CanonicalPayloadJson = replay.CanonicalPayloadJson,
            MetadataJson = JsonSerializer.Serialize(
                new
                {
                    sourceTransactionId = transaction.Id,
                    sourceExternalTransactionId = transaction.ExternalTransactionId,
                    originalCorrelationId = transaction.CorrelationId,
                },
                JsonOptions),
            OccurredAtUtc = now,
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return new TransactionReplayResult(
            replay.Id,
            replay.ExternalTransactionId,
            replay.CorrelationId,
            "Replay transaction created and queued for inspection.");
    }

    public async Task<PushTransactionsResult> RepushTransactionAsync(
        Guid transactionId,
        string? targetKey,
        CancellationToken cancellationToken = default)
    {
        SimulatedTransaction? transaction = await dbContext.SimulatedTransactions
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == transactionId, cancellationToken);

        if (transaction is null)
        {
            return new PushTransactionsResult(StatusCodes.Status404NotFound, "Transaction was not found.", 0, []);
        }

        return await forecourtSimulationService.PushTransactionsAsync(
            transaction.SiteId,
            new PushTransactionsRequest
            {
                TransactionIds = [transaction.ExternalTransactionId],
                TargetKey = Normalize(targetKey),
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<LogListItemView>> ListLogsAsync(
        LogListQuery query,
        CancellationToken cancellationToken = default)
    {
        int take = Math.Clamp(query.Limit, 1, 200);
        string? siteCode = Normalize(query.SiteCode);
        string? category = Normalize(query.Category);
        string? severity = Normalize(query.Severity);
        string? correlationId = Normalize(query.CorrelationId);
        string? search = Normalize(query.Search);

        IQueryable<LabEventLog> logQuery = dbContext.LabEventLogs
            .AsNoTracking();

        if (query.SiteId.HasValue)
        {
            logQuery = logQuery.Where(x => x.SiteId == query.SiteId.Value);
        }
        else if (!string.IsNullOrWhiteSpace(siteCode))
        {
            logQuery = logQuery.Where(x => x.Site != null && x.Site.SiteCode == siteCode);
        }

        if (query.ProfileId.HasValue)
        {
            logQuery = logQuery.Where(x => x.FccSimulatorProfileId == query.ProfileId.Value);
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            logQuery = logQuery.Where(x => x.Category == category);
        }

        if (!string.IsNullOrWhiteSpace(severity))
        {
            logQuery = logQuery.Where(x => x.Severity == severity);
        }

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            logQuery = logQuery.Where(x => x.CorrelationId == correlationId);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            logQuery = logQuery.Where(x =>
                x.CorrelationId.Contains(search) ||
                x.EventType.Contains(search) ||
                x.Message.Contains(search));
        }

        return await logQuery
            .OrderByDescending(x => x.OccurredAtUtc)
            .Take(take)
            .Select(x => new LogListItemView(
                x.Id,
                x.SiteId,
                x.Site != null ? x.Site.SiteCode : null,
                x.FccSimulatorProfileId,
                x.FccSimulatorProfile != null ? x.FccSimulatorProfile.ProfileKey : null,
                x.SimulatedTransactionId,
                x.PreAuthSessionId,
                x.Category,
                x.EventType,
                x.Severity,
                x.Message,
                x.CorrelationId,
                x.OccurredAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<LogDetailView?> GetLogAsync(Guid logId, CancellationToken cancellationToken = default)
    {
        LabEventLog? log = await dbContext.LabEventLogs
            .AsNoTracking()
            .Include(x => x.Site)
            .Include(x => x.FccSimulatorProfile)
            .Include(x => x.PreAuthSession)
            .Include(x => x.SimulatedTransaction)
            .SingleOrDefaultAsync(x => x.Id == logId, cancellationToken);

        if (log is null)
        {
            return null;
        }

        CallbackAttempt? callbackAttempt = await ResolveCallbackAttemptAsync(log, cancellationToken);

        string? requestHeadersJson = null;
        string? requestPayloadJson = null;
        string? responseHeadersJson = null;
        string? responsePayloadJson = null;

        if (callbackAttempt is not null)
        {
            requestHeadersJson = SafeJson(callbackAttempt.RequestHeadersJson);
            requestPayloadJson = SafeJson(callbackAttempt.RequestPayloadJson);
            responseHeadersJson = SafeJson(callbackAttempt.ResponseHeadersJson);
            responsePayloadJson = SafeJson(callbackAttempt.ResponsePayloadJson);
        }
        else if (log.PreAuthSession is not null)
        {
            requestPayloadJson = SafeJson(log.PreAuthSession.RawRequestJson);
            responsePayloadJson = SafeJson(log.PreAuthSession.RawResponseJson);
        }

        if (string.Equals(log.Category, "FccRequest", StringComparison.OrdinalIgnoreCase))
        {
            requestPayloadJson ??= SafeJson(log.RawPayloadJson);
        }

        if (string.Equals(log.Category, "FccResponse", StringComparison.OrdinalIgnoreCase))
        {
            responsePayloadJson ??= SafeJson(log.RawPayloadJson);
            requestPayloadJson ??= ExtractJsonProperty(log.MetadataJson, "requestPayload");
        }

        if (string.Equals(log.EventType, "CallbackCaptured", StringComparison.OrdinalIgnoreCase))
        {
            requestPayloadJson ??= SafeJson(log.RawPayloadJson);
            requestHeadersJson ??= ExtractJsonProperty(log.MetadataJson, "requestHeaders");
        }

        return new LogDetailView(
            log.Id,
            log.SiteId,
            log.Site?.SiteCode,
            log.FccSimulatorProfileId,
            log.FccSimulatorProfile?.ProfileKey,
            log.FccSimulatorProfile?.Name,
            log.SimulatedTransactionId,
            log.SimulatedTransaction?.ExternalTransactionId,
            log.PreAuthSessionId,
            log.Category,
            log.EventType,
            log.Severity,
            log.Message,
            log.CorrelationId,
            log.OccurredAtUtc,
            SafeJson(log.RawPayloadJson),
            SafeJson(log.CanonicalPayloadJson),
            SafeJson(log.MetadataJson),
            requestHeadersJson,
            requestPayloadJson,
            responseHeadersJson,
            responsePayloadJson);
    }

    private async Task<CallbackAttempt?> ResolveCallbackAttemptAsync(LabEventLog log, CancellationToken cancellationToken)
    {
        Guid? linkedAttemptId = TryReadGuid(log.MetadataJson, "linkedAttemptId") ?? TryReadGuid(log.MetadataJson, "attemptId");
        if (linkedAttemptId.HasValue)
        {
            return await dbContext.CallbackAttempts
                .AsNoTracking()
                .Include(x => x.CallbackTarget)
                .SingleOrDefaultAsync(x => x.Id == linkedAttemptId.Value, cancellationToken);
        }

        if (!log.SimulatedTransactionId.HasValue)
        {
            return null;
        }

        int? attemptNumber = TryReadInt(log.MetadataJson, "attemptNumber");
        string? targetKey = TryReadString(log.MetadataJson, "targetKey");

        IQueryable<CallbackAttempt> attemptQuery = dbContext.CallbackAttempts
            .AsNoTracking()
            .Include(x => x.CallbackTarget)
            .Where(x => x.SimulatedTransactionId == log.SimulatedTransactionId.Value);

        if (attemptNumber.HasValue)
        {
            attemptQuery = attemptQuery.Where(x => x.AttemptNumber == attemptNumber.Value);
        }

        if (!string.IsNullOrWhiteSpace(targetKey))
        {
            attemptQuery = attemptQuery.Where(x => x.CallbackTarget.TargetKey == targetKey);
        }

        return await attemptQuery
            .OrderByDescending(x => x.AttemptedAtUtc)
            .ThenByDescending(x => x.AttemptNumber)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static IReadOnlyList<TransactionTimelineEntryView> BuildTimeline(
        SimulatedTransaction transaction,
        IReadOnlyList<LabEventLog> logs,
        IEnumerable<CallbackAttempt> callbackAttempts)
    {
        List<TransactionTimelineEntryView> entries = [];

        foreach (JsonElement element in DeserializeArray(transaction.TimelineJson))
        {
            DateTimeOffset occurredAtUtc = ReadDateTimeOffset(element, "atUtc") ??
                ReadDateTimeOffset(element, "occurredAtUtc") ??
                transaction.OccurredAtUtc;

            entries.Add(new TransactionTimelineEntryView(
                "domain-event",
                occurredAtUtc,
                "DomainEvent",
                ReadString(element, "event") ?? ReadString(element, "eventType") ?? "Unknown",
                "Information",
                ReadString(element, "state") ?? ReadString(element, "status") ?? string.Empty,
                ReadString(element, "message") ?? string.Empty,
                ReadJson(element, "metadata")));
        }

        foreach (LabEventLog log in logs)
        {
            entries.Add(new TransactionTimelineEntryView(
                "log",
                log.OccurredAtUtc,
                log.Category,
                log.EventType,
                log.Severity,
                string.Empty,
                log.Message,
                SafeJson(log.MetadataJson)));
        }

        foreach (CallbackAttempt attempt in callbackAttempts)
        {
            entries.Add(new TransactionTimelineEntryView(
                "callback-attempt",
                attempt.AttemptedAtUtc,
                "CallbackAttempt",
                "CallbackAttemptObserved",
                attempt.Status == CallbackAttemptStatus.Failed ? "Warning" : "Information",
                attempt.Status.ToString(),
                $"Callback attempt {attempt.AttemptNumber} for target '{attempt.CallbackTarget.TargetKey}' completed with status {attempt.Status}.",
                JsonSerializer.Serialize(
                    new
                    {
                        attempt.AttemptNumber,
                        targetKey = attempt.CallbackTarget.TargetKey,
                        attempt.Status,
                        attempt.ResponseStatusCode,
                        attempt.RetryCount,
                        attempt.MaxRetryCount,
                        attempt.NextRetryAtUtc,
                    },
                    JsonOptions)));
        }

        return entries
            .OrderBy(x => x.OccurredAtUtc)
            .ThenBy(x => x.Source, StringComparer.Ordinal)
            .ToArray();
    }

    private static TransactionCallbackAttemptView MapCallbackAttempt(CallbackAttempt attempt)
    {
        return new TransactionCallbackAttemptView(
            attempt.Id,
            attempt.CallbackTargetId,
            attempt.CallbackTarget.TargetKey,
            attempt.CallbackTarget.Name,
            attempt.CorrelationId,
            attempt.AttemptNumber,
            attempt.Status,
            attempt.ResponseStatusCode,
            attempt.RequestUrl,
            SafeJson(attempt.RequestHeadersJson),
            SafeJson(attempt.RequestPayloadJson),
            SafeJson(attempt.ResponseHeadersJson),
            SafeJson(attempt.ResponsePayloadJson),
            attempt.ErrorMessage,
            attempt.RetryCount,
            attempt.MaxRetryCount,
            attempt.AttemptedAtUtc,
            attempt.CompletedAtUtc,
            attempt.NextRetryAtUtc,
            attempt.AcknowledgedAtUtc);
    }

    private static string BuildReplayCorrelationId(
        SimulatedTransaction transaction,
        string? requestedCorrelationId,
        int replayOrdinal)
    {
        string? normalized = Normalize(requestedCorrelationId);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        string proposed = $"{transaction.CorrelationId}-replay-{replayOrdinal:D2}";
        return proposed.Length <= 64 ? proposed : proposed[..64];
    }

    private static string PatchJson(string json, IReadOnlyDictionary<string, object?> replacements)
    {
        JsonNode? node;

        try
        {
            node = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        }
        catch (JsonException)
        {
            return SafeJson(json);
        }

        if (node is not JsonObject jsonObject)
        {
            return SafeJson(json);
        }

        foreach (KeyValuePair<string, object?> replacement in replacements)
        {
            jsonObject[replacement.Key] = replacement.Value switch
            {
                null => null,
                JsonNode jsonNode => jsonNode,
                _ => JsonSerializer.SerializeToNode(replacement.Value, JsonOptions),
            };
        }

        return jsonObject.ToJsonString(JsonOptions);
    }

    private static string? ExtractJsonProperty(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(propertyName, out JsonElement value))
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.String
                ? SafeJson(value.GetString())
                : value.GetRawText();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Guid? TryReadGuid(string json, string propertyName)
    {
        string? value = TryReadString(json, propertyName);
        return Guid.TryParse(value, out Guid parsed) ? parsed : null;
    }

    private static int? TryReadInt(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(propertyName, out JsonElement value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt32(out int parsed) => parsed,
                JsonValueKind.String when int.TryParse(value.GetString(), out int parsed) => parsed,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryReadString(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(propertyName, out JsonElement value)
                ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<JsonElement> DeserializeArray(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return document.RootElement.EnumerateArray()
                .Select(x => x.Clone())
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out DateTimeOffset parsed)
            ? parsed
            : null;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    private static string ReadJson(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return "{}";
        }

        return value.ValueKind == JsonValueKind.Null ? "null" : value.GetRawText();
    }

    private static FccProfileContract BuildValidationContract(string validationRulesJson, string fieldMappingsJson)
    {
        return new FccProfileContract
        {
            ValidationRules = DeserializeJson(validationRulesJson, new List<FccValidationRuleDefinition>()),
            FieldMappings = DeserializeJson(fieldMappingsJson, new List<FccFieldMappingDefinition>()),
        };
    }

    private static string SafeJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new { value = json }, JsonOptions);
        }
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static T DeserializeJson<T>(string json, T fallback)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return fallback;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }
}
