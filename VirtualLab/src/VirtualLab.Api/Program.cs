using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using VirtualLab.Application;
using VirtualLab.Application.Diagnostics;
using VirtualLab.Application.FccProfiles;
using VirtualLab.Application.Forecourt;
using VirtualLab.Application.Management;
using VirtualLab.Application.Observability;
using VirtualLab.Application.PreAuth;
using VirtualLab.Application.Callbacks;
using VirtualLab.Application.Scenarios;
using VirtualLab.Domain.Benchmarking;
using VirtualLab.Domain.Enums;
using VirtualLab.Domain.Models;
using VirtualLab.Infrastructure;
using VirtualLab.Infrastructure.Auth;
using VirtualLab.Infrastructure.Diagnostics;
using VirtualLab.Infrastructure.DomsJpl;
using VirtualLab.Infrastructure.FccProfiles;
using VirtualLab.Infrastructure.AdvatecSimulator;
using VirtualLab.Infrastructure.PetroniteSimulator;
using VirtualLab.Infrastructure.RadixSimulator;
using VirtualLab.Infrastructure.Persistence;
using VirtualLab.Infrastructure.Persistence.Seed;
using VirtualLab.Api.Hubs;
using VirtualLab.Api;

var builder = WebApplication.CreateBuilder(args);

string benchmarkSeedPath = ResolveBenchmarkSeedPath(builder.Environment.ContentRootPath);
builder.Configuration.AddJsonFile(benchmarkSeedPath, optional: false, reloadOnChange: false);
builder.Services.Configure<BenchmarkSeedProfile>(builder.Configuration);
builder.Services.AddVirtualLabApplication();
builder.Services.AddVirtualLabInfrastructure(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

await using (AsyncServiceScope scope = app.Services.CreateAsyncScope())
{
    VirtualLabDbContext dbContext = scope.ServiceProvider.GetRequiredService<VirtualLabDbContext>();
    IOptions<VirtualLabSeedOptions> seedOptions = scope.ServiceProvider.GetRequiredService<IOptions<VirtualLabSeedOptions>>();
    IVirtualLabSeedService seedService = scope.ServiceProvider.GetRequiredService<IVirtualLabSeedService>();

    await dbContext.Database.MigrateAsync();

    if (seedOptions.Value.ApplyOnStartup)
    {
        await seedService.SeedAsync(seedOptions.Value.ResetOnStartup);
    }
}

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors(VirtualLabCorsOptions.PolicyName);
app.UseMiddleware<ApiTimingMiddleware>();
app.UseMiddleware<InboundAuthSimulationMiddleware>();

app.MapGet("/healthz", () => Results.Ok(new
{
    status = "Healthy",
    service = "VirtualLab.Api",
    environment = app.Environment.EnvironmentName,
}));

app.MapGet("/api/dashboard", async (
    VirtualLabDbContext dbContext,
    IVirtualLabManagementService managementService,
    IOptions<BenchmarkSeedProfile> profile,
    CancellationToken cancellationToken) =>
{
    BenchmarkSeedProfile seed = profile.Value;
    DateTimeOffset now = DateTimeOffset.UtcNow;
    DateTimeOffset sinceUtc = now.AddHours(-24);

    IReadOnlyList<SiteListItemView> sites = await managementService.ListSitesAsync(true, cancellationToken);

    SimulatedTransactionStatus[] activeStatuses =
    [
        SimulatedTransactionStatus.Created,
        SimulatedTransactionStatus.ReadyForDelivery,
        SimulatedTransactionStatus.Delivered,
    ];

    int activeTransactionCount = await dbContext.SimulatedTransactions
        .AsNoTracking()
        .CountAsync(x => activeStatuses.Contains(x.Status), cancellationToken);

    var activeTransactions = await dbContext.SimulatedTransactions
        .AsNoTracking()
        .Include(x => x.Site)
        .Include(x => x.Pump)
        .Include(x => x.Nozzle)
        .Include(x => x.Product)
        .Where(x => activeStatuses.Contains(x.Status))
        .OrderByDescending(x => x.OccurredAtUtc)
        .Take(8)
        .Select(x => new
        {
            x.Id,
            SiteCode = x.Site.SiteCode,
            x.CorrelationId,
            x.ExternalTransactionId,
            x.Status,
            x.DeliveryMode,
            PumpNumber = x.Pump.PumpNumber,
            NozzleNumber = x.Nozzle.NozzleNumber,
            ProductCode = x.Product.ProductCode,
            x.Volume,
            x.TotalAmount,
            x.OccurredAtUtc,
        })
        .ToListAsync(cancellationToken);

    int authFailureCount = await dbContext.LabEventLogs
        .AsNoTracking()
        .CountAsync(x => x.Category == "AuthFailure" && x.OccurredAtUtc >= sinceUtc, cancellationToken);

    var authFailures = await dbContext.LabEventLogs
        .AsNoTracking()
        .Include(x => x.Site)
        .Where(x => x.Category == "AuthFailure")
        .OrderByDescending(x => x.OccurredAtUtc)
        .Take(6)
        .Select(x => new
        {
            x.Id,
            SiteCode = x.Site != null ? x.Site.SiteCode : null,
            x.EventType,
            x.Message,
            x.CorrelationId,
            x.OccurredAtUtc,
        })
        .ToListAsync(cancellationToken);

    int callbackSucceeded = await dbContext.CallbackAttempts
        .AsNoTracking()
        .CountAsync(x => x.AttemptedAtUtc >= sinceUtc && x.Status == CallbackAttemptStatus.Succeeded, cancellationToken);

    int callbackFailed = await dbContext.CallbackAttempts
        .AsNoTracking()
        .CountAsync(x => x.AttemptedAtUtc >= sinceUtc && x.Status == CallbackAttemptStatus.Failed, cancellationToken);

    int callbackPending = await dbContext.CallbackAttempts
        .AsNoTracking()
        .CountAsync(
            x => x.AttemptedAtUtc >= sinceUtc &&
                 (x.Status == CallbackAttemptStatus.Pending || x.Status == CallbackAttemptStatus.InProgress),
            cancellationToken);

    double callbackSuccessRate = callbackSucceeded + callbackFailed == 0
        ? 100
        : Math.Round((double)callbackSucceeded / (callbackSucceeded + callbackFailed) * 100, 1);

    var recentCallbackAttempts = await dbContext.CallbackAttempts
        .AsNoTracking()
        .Include(x => x.CallbackTarget)
        .Include(x => x.SimulatedTransaction)
            .ThenInclude(x => x.Site)
        .OrderByDescending(x => x.AttemptedAtUtc)
        .Take(6)
        .Select(x => new
        {
            x.Id,
            SiteCode = x.SimulatedTransaction.Site.SiteCode,
            TargetKey = x.CallbackTarget.TargetKey,
            x.CorrelationId,
            x.AttemptNumber,
            x.Status,
            x.ResponseStatusCode,
            x.ErrorMessage,
            x.AttemptedAtUtc,
            x.NextRetryAtUtc,
        })
        .ToListAsync(cancellationToken);

    var recentAlerts = await dbContext.LabEventLogs
        .AsNoTracking()
        .Include(x => x.Site)
        .Where(x => x.OccurredAtUtc >= sinceUtc && (x.Severity != "Information" || x.Category == "AuthFailure" || x.Category == "CallbackFailure"))
        .OrderByDescending(x => x.OccurredAtUtc)
        .Take(10)
        .Select(x => new
        {
            x.Id,
            SiteCode = x.Site != null ? x.Site.SiteCode : null,
            x.Category,
            x.EventType,
            x.Severity,
            x.Message,
            x.CorrelationId,
            x.OccurredAtUtc,
        })
        .ToListAsync(cancellationToken);

    return Results.Ok(new
    {
        refreshedAtUtc = now,
        profileName = seed.ProfileName,
        seedTargets = new
        {
            sites = seed.Sites,
            pumps = seed.TotalPumps,
            nozzles = seed.TotalNozzles,
            transactions = seed.Transactions,
        },
        sites = sites,
        activeTransactions = new
        {
            total = activeTransactionCount,
            items = activeTransactions,
        },
        authFailures = new
        {
            last24Hours = authFailureCount,
            items = authFailures,
        },
        callbackDelivery = new
        {
            succeededLast24Hours = callbackSucceeded,
            failedLast24Hours = callbackFailed,
            pending = callbackPending,
            successRatePercent = callbackSuccessRate,
            items = recentCallbackAttempts,
        },
        recentAlerts,
    });
});

app.MapVirtualLabManagementEndpoints();
app.MapDomsJplManagementEndpoints();
app.MapRadixManagementEndpoints();
app.MapPetroniteManagementEndpoints();
app.MapAdvatecManagementEndpoints();

app.MapGet("/api/fcc-profiles", async (IFccProfileService profileService, CancellationToken cancellationToken) =>
{
    return Results.Ok(await profileService.ListAsync(cancellationToken));
});

app.MapGet("/api/fcc-profiles/{id:guid}", async (Guid id, IFccProfileService profileService, CancellationToken cancellationToken) =>
{
    FccProfileRecord? profile = await profileService.GetAsync(id, cancellationToken);
    return profile is null ? Results.NotFound() : Results.Ok(profile);
});

app.MapGet("/api/fcc-profiles/site/{siteCode}/resolved", async (string siteCode, IFccProfileService profileService, CancellationToken cancellationToken) =>
{
    ResolvedFccProfile? profile = await profileService.ResolveBySiteCodeAsync(siteCode, cancellationToken);
    return profile is null ? Results.NotFound() : Results.Ok(profile);
});

app.MapPost("/api/fcc-profiles/validate", async (FccProfileRecord record, IFccProfileService profileService, CancellationToken cancellationToken) =>
{
    return Results.Ok(await profileService.ValidateAsync(record, cancellationToken));
});

app.MapPost("/api/fcc-profiles/preview", async (FccProfilePreviewRequest request, IFccProfileService profileService, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await profileService.PreviewAsync(request, cancellationToken));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
});

app.MapPost("/api/fcc-profiles", async (FccProfileRecord record, IFccProfileService profileService, CancellationToken cancellationToken) =>
{
    try
    {
        FccProfileRecord created = await profileService.CreateAsync(record, cancellationToken);
        return Results.Created($"/api/fcc-profiles/{created.Id}", created);
    }
    catch (FccProfileValidationException exception)
    {
        return Results.BadRequest(exception.ValidationResult);
    }
});

app.MapPut("/api/fcc-profiles/{id:guid}", async (Guid id, FccProfileRecord record, IFccProfileService profileService, CancellationToken cancellationToken) =>
{
    try
    {
        FccProfileRecord? updated = await profileService.UpdateAsync(id, record, cancellationToken);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }
    catch (FccProfileValidationException exception)
    {
        return Results.BadRequest(exception.ValidationResult);
    }
});

app.MapGet("/api/sites/{siteCode}/forecourt", async (string siteCode, VirtualLabDbContext dbContext) =>
{
    var site = await dbContext.Sites
        .AsNoTracking()
        .Include(x => x.Pumps.OrderBy(p => p.PumpNumber))
            .ThenInclude(x => x.Nozzles.OrderBy(n => n.NozzleNumber))
                .ThenInclude(x => x.Product)
        .FirstOrDefaultAsync(x => x.SiteCode == siteCode);

    return site is null
        ? Results.NotFound()
        : Results.Ok(new
        {
            site.Id,
            site.SiteCode,
            site.Name,
            Pumps = site.Pumps
                .OrderBy(p => p.PumpNumber)
                .Select(pump => new
                {
                    pump.Id,
                    pump.PumpNumber,
                    pump.Label,
                    Nozzles = pump.Nozzles
                        .OrderBy(nozzle => nozzle.NozzleNumber)
                        .Select(nozzle => new
                        {
                            nozzle.Id,
                            nozzle.NozzleNumber,
                            nozzle.Label,
                            nozzle.State,
                            Product = new
                            {
                                nozzle.Product.ProductCode,
                                nozzle.Product.Name,
                                nozzle.Product.UnitPrice,
                            },
                        }),
                }),
        });
});

RouteGroupBuilder fccGroup = app.MapGroup("/fcc/{siteCode}");

fccGroup.MapGet("/health", async (string siteCode, IForecourtSimulationService forecourtSimulationService, CancellationToken cancellationToken) =>
{
    FccEndpointResult result = await forecourtSimulationService.GetHealthAsync(siteCode, cancellationToken);
    return Results.Content(result.ResponseBody, result.ContentType, Encoding.UTF8, result.StatusCode);
});

fccGroup.MapGet("/transactions", async (string siteCode, int? limit, string? cursor, IForecourtSimulationService forecourtSimulationService, CancellationToken cancellationToken) =>
{
    PullTransactionsResult result = await forecourtSimulationService.PullTransactionsAsync(siteCode, limit ?? 100, cursor, cancellationToken);
    return Results.Content(result.ResponseBody, result.ContentType, Encoding.UTF8, result.StatusCode);
});

fccGroup.MapPost("/transactions/ack", async (string siteCode, AcknowledgeTransactionsRequest request, IForecourtSimulationService forecourtSimulationService, CancellationToken cancellationToken) =>
{
    AcknowledgeTransactionsResult result = await forecourtSimulationService.AcknowledgeTransactionsAsync(siteCode, request, cancellationToken);
    return Results.Content(result.ResponseBody, result.ContentType, Encoding.UTF8, result.StatusCode);
});

fccGroup.MapGet("/pump-status", async (string siteCode, IForecourtSimulationService forecourtSimulationService, CancellationToken cancellationToken) =>
{
    FccEndpointResult result = await forecourtSimulationService.GetPumpStatusAsync(siteCode, cancellationToken);
    return Results.Content(result.ResponseBody, result.ContentType, Encoding.UTF8, result.StatusCode);
});

fccGroup.MapPost("/preauth/create", async (string siteCode, HttpContext httpContext, IPreAuthSimulationService preAuthService, CancellationToken cancellationToken) =>
    await HandlePreAuthAsync(siteCode, "preauth-create", httpContext, preAuthService, cancellationToken));

fccGroup.MapPost("/preauth/authorize", async (string siteCode, HttpContext httpContext, IPreAuthSimulationService preAuthService, CancellationToken cancellationToken) =>
    await HandlePreAuthAsync(siteCode, "preauth-authorize", httpContext, preAuthService, cancellationToken));

fccGroup.MapPost("/preauth/cancel", async (string siteCode, HttpContext httpContext, IPreAuthSimulationService preAuthService, CancellationToken cancellationToken) =>
    await HandlePreAuthAsync(siteCode, "preauth-cancel", httpContext, preAuthService, cancellationToken));

app.MapGet("/api/transactions", async (
    Guid? siteId,
    string? siteCode,
    string? correlationId,
    string? search,
    TransactionDeliveryMode? deliveryMode,
    SimulatedTransactionStatus? status,
    int? limit,
    IObservabilityService observabilityService,
    CancellationToken cancellationToken) =>
{
    IReadOnlyList<TransactionListItemView> transactions = await observabilityService.ListTransactionsAsync(
        new TransactionListQuery
        {
            SiteId = siteId,
            SiteCode = siteCode,
            CorrelationId = correlationId,
            Search = search,
            DeliveryMode = deliveryMode,
            Status = status,
            Limit = limit ?? 50,
        },
        cancellationToken);

    return Results.Ok(transactions);
});

app.MapGet("/api/transactions/{id:guid}", async (Guid id, IObservabilityService observabilityService, CancellationToken cancellationToken) =>
{
    TransactionDetailView? transaction = await observabilityService.GetTransactionAsync(id, cancellationToken);
    return transaction is null ? Results.NotFound() : Results.Ok(transaction);
});

app.MapPost("/api/transactions/{id:guid}/replay", async (
    Guid id,
    TransactionReplayRequest? request,
    IObservabilityService observabilityService,
    CancellationToken cancellationToken) =>
{
    TransactionReplayResult? result = await observabilityService.ReplayTransactionAsync(
        id,
        request ?? new TransactionReplayRequest(null),
        cancellationToken);

    return result is null
        ? Results.NotFound()
        : Results.Created($"/api/transactions/{result.TransactionId:D}", result);
});

app.MapPost("/api/transactions/{id:guid}/re-push", async (
    Guid id,
    PushTransactionsRequest? request,
    IObservabilityService observabilityService,
    CancellationToken cancellationToken) =>
{
    PushTransactionsResult result = await observabilityService.RepushTransactionAsync(
        id,
        request?.TargetKey,
        cancellationToken);

    return Results.Json(result, statusCode: result.StatusCode);
});

app.MapPost("/api/sites/{siteId:guid}/pumps/{pumpId:guid}/nozzles/{nozzleId:guid}/lift", async (
    Guid siteId,
    Guid pumpId,
    Guid nozzleId,
    NozzleLiftRequest? request,
    IForecourtSimulationService forecourtSimulationService,
    IHubContext<LabLiveHub> hubContext,
    CancellationToken cancellationToken) =>
{
    NozzleActionResult result = await forecourtSimulationService.LiftAsync(siteId, pumpId, nozzleId, request ?? new NozzleLiftRequest(), cancellationToken);
    if (result.Nozzle is not null && result.StatusCode is >= 200 and < 300)
    {
        await BroadcastForecourtUpdateAsync(hubContext, "lift", result, cancellationToken);
    }

    return Results.Json(result, statusCode: result.StatusCode);
});

app.MapPost("/api/sites/{siteId:guid}/pumps/{pumpId:guid}/nozzles/{nozzleId:guid}/hang", async (
    Guid siteId,
    Guid pumpId,
    Guid nozzleId,
    NozzleHangRequest? request,
    IForecourtSimulationService forecourtSimulationService,
    IHubContext<LabLiveHub> hubContext,
    CancellationToken cancellationToken) =>
{
    NozzleActionResult result = await forecourtSimulationService.HangAsync(siteId, pumpId, nozzleId, request ?? new NozzleHangRequest(), cancellationToken);
    if (result.Nozzle is not null && result.StatusCode is >= 200 and < 300)
    {
        await BroadcastForecourtUpdateAsync(hubContext, "hang", result, cancellationToken);
    }

    return Results.Json(result, statusCode: result.StatusCode);
});

app.MapPost("/api/sites/{siteId:guid}/pumps/{pumpId:guid}/nozzles/{nozzleId:guid}/dispense", async (
    Guid siteId,
    Guid pumpId,
    Guid nozzleId,
    DispenseSimulationRequest? request,
    IForecourtSimulationService forecourtSimulationService,
    IHubContext<LabLiveHub> hubContext,
    CancellationToken cancellationToken) =>
{
    NozzleActionResult result = await forecourtSimulationService.DispenseAsync(siteId, pumpId, nozzleId, request ?? new DispenseSimulationRequest(), cancellationToken);
    if (result.Nozzle is not null && result.StatusCode is >= 200 and < 300)
    {
        await BroadcastForecourtUpdateAsync(hubContext, "dispense", result, cancellationToken);
    }

    return Results.Json(result, statusCode: result.StatusCode);
});

app.MapPost("/api/sites/{siteId:guid}/preauth/simulate", async (
    Guid siteId,
    LabPreAuthActionRequest? request,
    VirtualLabDbContext dbContext,
    IPreAuthSimulationService preAuthService,
    IHubContext<LabLiveHub> hubContext,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    Site? site = await dbContext.Sites
        .AsNoTracking()
        .SingleOrDefaultAsync(x => x.Id == siteId && x.IsActive, cancellationToken);

    if (site is null)
    {
        return Results.NotFound();
    }

    LabPreAuthActionRequest actionRequest = request ?? new LabPreAuthActionRequest();
    string action = string.IsNullOrWhiteSpace(actionRequest.Action)
        ? "create"
        : actionRequest.Action.Trim().ToLowerInvariant();
    string correlationId = string.IsNullOrWhiteSpace(actionRequest.CorrelationId)
        ? httpContext.TraceIdentifier
        : actionRequest.CorrelationId;

    if (action == "expire")
    {
        if (string.IsNullOrWhiteSpace(actionRequest.PreAuthId))
        {
            LabPreAuthActionResult invalidResult = new(
                StatusCodes.Status400BadRequest,
                action,
                "preAuthId is required for expire.",
                site.SiteCode,
                correlationId,
                """{"message":"preAuthId is required for expire."}""",
                null);

            return Results.Json(invalidResult, statusCode: invalidResult.StatusCode);
        }

        PreAuthSessionSummary? expiredSession = await preAuthService.ExpireSessionAsync(
            new PreAuthManualExpiryRequest
            {
                SiteCode = site.SiteCode,
                PreAuthId = actionRequest.PreAuthId,
                CorrelationId = correlationId,
            },
            cancellationToken);

        if (expiredSession is null)
        {
            LabPreAuthActionResult notFoundResult = new(
                StatusCodes.Status404NotFound,
                action,
                $"Pre-auth id '{actionRequest.PreAuthId}' was not found.",
                site.SiteCode,
                correlationId,
                JsonSerializer.Serialize(new { message = $"Pre-auth id '{actionRequest.PreAuthId}' was not found." }),
                null);

            return Results.Json(notFoundResult, statusCode: notFoundResult.StatusCode);
        }

        int statusCode = string.Equals(expiredSession.Status, "EXPIRED", StringComparison.OrdinalIgnoreCase)
            ? StatusCodes.Status200OK
            : StatusCodes.Status409Conflict;
        string message = statusCode == StatusCodes.Status200OK
            ? "Pre-auth session expired."
            : $"Session '{expiredSession.ExternalReference}' is already {expiredSession.Status}.";
        string responseBody = JsonSerializer.Serialize(new
        {
            status = expiredSession.Status.ToLowerInvariant(),
            preauthId = expiredSession.ExternalReference,
            correlationId = expiredSession.CorrelationId,
            message,
        });

        LabPreAuthActionResult expireResult = new(
            statusCode,
            action,
            message,
            site.SiteCode,
            expiredSession.CorrelationId,
            responseBody,
            expiredSession);

        if (statusCode is >= 200 and < 300)
        {
            await BroadcastPreAuthUpdateAsync(hubContext, expireResult, cancellationToken);
        }

        return Results.Json(expireResult, statusCode: expireResult.StatusCode);
    }

    string? operation = action switch
    {
        "create" => "preauth-create",
        "authorize" => "preauth-authorize",
        "cancel" => "preauth-cancel",
        _ => null,
    };

    if (operation is null)
    {
        LabPreAuthActionResult invalidActionResult = new(
            StatusCodes.Status400BadRequest,
            action,
            $"Unsupported pre-auth action '{actionRequest.Action}'.",
            site.SiteCode,
            correlationId,
            JsonSerializer.Serialize(new { message = $"Unsupported pre-auth action '{actionRequest.Action}'." }),
            null);

        return Results.Json(invalidActionResult, statusCode: invalidActionResult.StatusCode);
    }

    string requestBody = BuildLabPreAuthPayload(actionRequest, correlationId);
    Dictionary<string, string> fields = ExtractSampleValues(requestBody);
    PreAuthSimulationResponse response = await preAuthService.HandleAsync(
        new PreAuthSimulationRequest(
            site.SiteCode,
            operation,
            HttpMethods.Post,
            httpContext.Request.Path,
            httpContext.TraceIdentifier,
            requestBody,
            fields),
        cancellationToken);

    PreAuthSessionSummary? session = await preAuthService.GetSessionAsync(
        site.SiteCode,
        correlationId,
        actionRequest.PreAuthId,
        cancellationToken);
    LabPreAuthActionResult result = new(
        response.StatusCode,
        action,
        ResolveLabPreAuthMessage(action, response.Body),
        site.SiteCode,
        session?.CorrelationId ?? correlationId,
        response.Body,
        session);

    if (session is not null)
    {
        await BroadcastPreAuthUpdateAsync(hubContext, result, cancellationToken);
    }

    return Results.Json(result, statusCode: result.StatusCode);
});

app.MapPost("/api/sites/{siteId:guid}/transactions/push", async (
    Guid siteId,
    PushTransactionsRequest? request,
    IForecourtSimulationService forecourtSimulationService,
    CancellationToken cancellationToken) =>
{
    PushTransactionsResult result = await forecourtSimulationService.PushTransactionsAsync(siteId, request ?? new PushTransactionsRequest(), cancellationToken);
    return Results.Json(result, statusCode: result.StatusCode);
});

app.MapGet("/api/logs", async (
    Guid? siteId,
    string? siteCode,
    Guid? profileId,
    string? category,
    string? severity,
    string? correlationId,
    string? search,
    int? limit,
    IObservabilityService observabilityService,
    CancellationToken cancellationToken) =>
{
    IReadOnlyList<LogListItemView> logs = await observabilityService.ListLogsAsync(
        new LogListQuery
        {
            SiteId = siteId,
            SiteCode = siteCode,
            ProfileId = profileId,
            Category = category,
            Severity = severity,
            CorrelationId = correlationId,
            Search = search,
            Limit = limit ?? 100,
        },
        cancellationToken);

    return Results.Ok(logs);
});

app.MapGet("/api/logs/{id:guid}", async (Guid id, IObservabilityService observabilityService, CancellationToken cancellationToken) =>
{
    LogDetailView? log = await observabilityService.GetLogAsync(id, cancellationToken);
    return log is null ? Results.NotFound() : Results.Ok(log);
});

app.MapGet("/api/preauth-sessions", async (string? siteCode, string? correlationId, int? limit, IPreAuthSimulationService preAuthService, CancellationToken cancellationToken) =>
{
    int take = Math.Clamp(limit ?? 100, 1, 200);
    IReadOnlyList<PreAuthSessionSummary> sessions = await preAuthService.ListSessionsAsync(siteCode, correlationId, take, cancellationToken);
    return Results.Ok(sessions);
});

app.MapGet("/api/scenarios", async (IScenarioService scenarioService, CancellationToken cancellationToken) =>
{
    return Results.Ok(new
    {
        definitions = await scenarioService.ListScenariosAsync(cancellationToken),
        runs = await scenarioService.ListRunsAsync(20, cancellationToken),
    });
});

app.MapPost("/api/scenarios/run", async (ScenarioRunRequest request, IScenarioService scenarioService, CancellationToken cancellationToken) =>
{
    ScenarioRunDetailView run = await scenarioService.RunAsync(request, cancellationToken);
    return Results.Created($"/api/scenarios/runs/{run.Id:D}", run);
});

app.MapGet("/api/scenarios/runs/{id:guid}", async (Guid id, IScenarioService scenarioService, CancellationToken cancellationToken) =>
{
    ScenarioRunDetailView? run = await scenarioService.GetRunAsync(id, cancellationToken);
    return run is null ? Results.NotFound() : Results.Ok(run);
});

app.MapGet("/api/scenarios/export", async (IScenarioService scenarioService, CancellationToken cancellationToken) =>
{
    IReadOnlyList<ScenarioDefinitionImportRecord> definitions = await scenarioService.ExportAsync(cancellationToken);
    return Results.Ok(definitions);
});

app.MapPost("/api/scenarios/import", async (ScenarioImportRequest request, IScenarioService scenarioService, CancellationToken cancellationToken) =>
{
    return Results.Ok(await scenarioService.ImportAsync(request, cancellationToken));
});

app.MapPost("/callbacks/{targetKey}", async (string targetKey, HttpContext httpContext, ICallbackCaptureService callbackCaptureService, CancellationToken cancellationToken) =>
{
    string requestBody = await ReadRequestBodyAsync(httpContext.Request, cancellationToken);
    string responsePayload = JsonSerializer.Serialize(new
    {
        accepted = true,
        targetKey,
        capturedAtUtc = DateTimeOffset.UtcNow,
    });

    Guid? linkedAttemptId = null;
    if (httpContext.Request.Headers.TryGetValue("X-VirtualLab-Attempt-Id", out Microsoft.Extensions.Primitives.StringValues attemptHeaderValue) &&
        Guid.TryParse(attemptHeaderValue.ToString(), out Guid parsedAttemptId))
    {
        linkedAttemptId = parsedAttemptId;
    }

    CallbackCaptureResult? capture = await callbackCaptureService.CaptureAsync(
        targetKey,
        new CallbackCaptureRequest
        {
            HttpMethod = httpContext.Request.Method,
            RequestUrl = httpContext.Request.Path.Value ?? $"/callbacks/{targetKey}",
            RequestHeadersJson = InboundAuthRequestSanitizer.SerializeHeaders(httpContext.Request.Headers),
            RequestPayloadJson = string.IsNullOrWhiteSpace(requestBody) ? "{}" : requestBody,
            AuthOutcome = "Authorized",
            AuthMode = "Inherited",
            ResponseStatusCode = StatusCodes.Status202Accepted,
            ResponseHeadersJson = """{"content-type":"application/json"}""",
            ResponsePayloadJson = responsePayload,
            LinkedAttemptId = linkedAttemptId,
        },
        cancellationToken);

    return capture is null
        ? Results.NotFound()
        : Results.Accepted($"/api/callbacks/{targetKey}/history", JsonSerializer.Deserialize<object>(capture.ResponsePayloadJson));
});

app.MapGet("/api/callbacks/{targetKey}/history", async (string targetKey, int? limit, ICallbackCaptureService callbackCaptureService, CancellationToken cancellationToken) =>
{
    return Results.Ok(await callbackCaptureService.ListHistoryAsync(targetKey, limit ?? 100, cancellationToken));
});

app.MapPost("/api/callbacks/{targetKey}/history/{id:guid}/replay", async (
    string targetKey,
    Guid id,
    ICallbackCaptureService callbackCaptureService,
    CancellationToken cancellationToken) =>
{
    CallbackReplayResult? result = await callbackCaptureService.ReplayAsync(targetKey, id, cancellationToken);
    return result is null
        ? Results.NotFound()
        : Results.Created($"/api/callbacks/{targetKey}/history/{result.CaptureId:D}", result);
});

app.MapGet("/api/diagnostics/latency", (int? iterations, DiagnosticProbeService probeService, ApiTimingStore timingStore) =>
{
    DiagnosticProbeResult probe = probeService.Run(iterations ?? 25);
    IReadOnlyDictionary<string, double> apiP95 = timingStore.GetP95ByRoute();

    return Results.Ok(new
    {
        probe.ProfileName,
        probe.ReplaySignature,
        thresholds = new
        {
            probe.Thresholds.StartupReadyMinutes,
            probe.Thresholds.DashboardLoadP95Ms,
            probe.Thresholds.SignalRUpdateP95Ms,
            probe.Thresholds.FccEmulatorP95Ms,
            probe.Thresholds.TransactionPullP95Ms,
        },
        measurements = new
        {
            probe.Measurements.DashboardQueryP95Ms,
            probe.Measurements.SiteLoadP95Ms,
            probe.Measurements.SignalRBroadcastP95Ms,
            probe.Measurements.FccHealthP95Ms,
            probe.Measurements.TransactionPullP95Ms,
            probe.Measurements.SampleCount,
            ApiP95ByRoute = apiP95,
        },
    });
});

app.MapPost("/api/diagnostics/live-broadcast", async (IHubContext<LabLiveHub> hubContext) =>
{
    var payload = new
    {
        eventType = "forecourt-action",
        occurredAtUtc = DateTimeOffset.UtcNow,
        correlationId = Guid.NewGuid().ToString("N"),
    };

    await hubContext.Clients.All.SendAsync("lab-event", payload);
    return Results.Accepted(value: payload);
});

app.MapHub<LabLiveHub>("/hubs/live");

app.Run();

static string ResolveBenchmarkSeedPath(string contentRootPath)
{
    string[] candidatePaths =
    [
        Path.Combine(contentRootPath, "config", "benchmark-seed.json"),
        Path.GetFullPath(Path.Combine(contentRootPath, "..", "..", "config", "benchmark-seed.json")),
    ];

    string? benchmarkSeedPath = candidatePaths.FirstOrDefault(File.Exists);
    if (benchmarkSeedPath is null)
    {
        throw new FileNotFoundException(
            "Unable to locate benchmark-seed.json for Virtual Lab startup.",
            candidatePaths[0]);
    }

    return benchmarkSeedPath;
}

static async Task BroadcastForecourtUpdateAsync(
    IHubContext<LabLiveHub> hubContext,
    string action,
    NozzleActionResult result,
    CancellationToken cancellationToken)
{
    await hubContext.Clients.All.SendAsync(
        "lab-event",
        new
        {
            eventType = "forecourt-action",
            action,
            occurredAtUtc = DateTimeOffset.UtcNow,
            correlationId = result.CorrelationId,
            message = result.Message,
            transactionGenerated = result.TransactionGenerated,
            faulted = result.Faulted,
            nozzle = result.Nozzle,
            transaction = result.Transaction,
        },
        cancellationToken);
}

static async Task BroadcastPreAuthUpdateAsync(
    IHubContext<LabLiveHub> hubContext,
    LabPreAuthActionResult result,
    CancellationToken cancellationToken)
{
    await hubContext.Clients.All.SendAsync(
        "lab-event",
        new
        {
            eventType = "preauth-action",
            action = result.Action,
            occurredAtUtc = DateTimeOffset.UtcNow,
            siteCode = result.SiteCode,
            correlationId = result.CorrelationId,
            responseStatusCode = result.StatusCode,
            message = result.Message,
            responseBody = result.ResponseBody,
            session = result.Session,
        },
        cancellationToken);
}

static async Task<IResult> HandlePreAuthAsync(
    string siteCode,
    string operation,
    HttpContext httpContext,
    IPreAuthSimulationService preAuthService,
    CancellationToken cancellationToken)
{
    string requestBody = await ReadRequestBodyAsync(httpContext.Request, cancellationToken);
    Dictionary<string, string> fields = await BuildSampleValuesAsync(httpContext.Request, siteCode, cancellationToken);
    PreAuthSimulationResponse response = await preAuthService.HandleAsync(
        new PreAuthSimulationRequest(
            siteCode,
            operation,
            httpContext.Request.Method,
            httpContext.Request.Path,
            httpContext.TraceIdentifier,
            requestBody,
            fields),
        cancellationToken);

    return Results.Content(response.Body, response.ContentType, statusCode: response.StatusCode);
}

static async Task<Dictionary<string, string>> BuildSampleValuesAsync(HttpRequest request, string siteCode, CancellationToken cancellationToken)
{
    Dictionary<string, string> sampleValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["siteCode"] = siteCode,
    };

    foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> queryValue in request.Query)
    {
        string queryText = queryValue.Value.ToString();
        if (!string.IsNullOrWhiteSpace(queryText))
        {
            sampleValues[queryValue.Key] = queryText;
        }
    }

    if (HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method))
    {
        string body = await ReadRequestBodyAsync(request, cancellationToken);
        foreach (KeyValuePair<string, string> bodyValue in ExtractSampleValues(body))
        {
            sampleValues[bodyValue.Key] = bodyValue.Value;
        }
    }

    if (!sampleValues.ContainsKey("correlationId"))
    {
        sampleValues["correlationId"] = request.HttpContext.TraceIdentifier;
    }

    return sampleValues;
}

static async Task<string> ReadRequestBodyAsync(HttpRequest request, CancellationToken cancellationToken)
{
    if (request.Body.CanSeek)
    {
        request.Body.Position = 0;
    }
    else
    {
        request.EnableBuffering();
    }

    using StreamReader reader = new(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
    string body = await reader.ReadToEndAsync();
    request.Body.Position = 0;
    return body;
}

static string BuildLabPreAuthPayload(LabPreAuthActionRequest request, string correlationId)
{
    return JsonSerializer.Serialize(new
    {
        preauthId = request.PreAuthId,
        correlationId,
        pump = request.PumpNumber,
        nozzle = request.NozzleNumber,
        amount = request.Amount,
        expiresInSeconds = request.ExpiresInSeconds,
        simulateFailure = request.SimulateFailure,
        failureStatusCode = request.FailureStatusCode,
        failureMessage = request.FailureMessage,
        failureCode = request.FailureCode,
        customerName = request.CustomerName,
        customerTaxId = request.CustomerTaxId,
        customerTaxOffice = request.CustomerTaxOffice,
    });
}

static Dictionary<string, string> ExtractSampleValues(string body)
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
                _ => property.Value.ToString(),
            };
        }
    }
    catch (JsonException)
    {
    }

    return values;
}

static string ResolveLabPreAuthMessage(string action, string responseBody)
{
    if (!string.IsNullOrWhiteSpace(responseBody))
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(responseBody);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (document.RootElement.TryGetProperty("message", out JsonElement messageElement) &&
                    messageElement.ValueKind == JsonValueKind.String)
                {
                    return messageElement.GetString() ?? $"Pre-auth {action} completed.";
                }

                if (document.RootElement.TryGetProperty("status", out JsonElement statusElement) &&
                    statusElement.ValueKind == JsonValueKind.String)
                {
                    string status = statusElement.GetString() ?? "completed";
                    return $"Pre-auth {action} returned {status}.";
                }
            }
        }
        catch (JsonException)
        {
        }
    }

    return $"Pre-auth {action} completed.";
}

public partial class Program
{
}
