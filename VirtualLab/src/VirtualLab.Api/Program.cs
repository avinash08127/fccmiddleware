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
using VirtualLab.Application.PreAuth;
using VirtualLab.Domain.Benchmarking;
using VirtualLab.Domain.Enums;
using VirtualLab.Domain.Models;
using VirtualLab.Infrastructure;
using VirtualLab.Infrastructure.Auth;
using VirtualLab.Infrastructure.Diagnostics;
using VirtualLab.Infrastructure.FccProfiles;
using VirtualLab.Infrastructure.Persistence;
using VirtualLab.Infrastructure.Persistence.Seed;
using VirtualLab.Api.Hubs;

var builder = WebApplication.CreateBuilder(args);

string benchmarkSeedPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "config", "benchmark-seed.json"));
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
app.UseMiddleware<ApiTimingMiddleware>();
app.UseMiddleware<InboundAuthSimulationMiddleware>();

app.MapGet("/healthz", () => Results.Ok(new
{
    status = "Healthy",
    service = "VirtualLab.Api",
    environment = app.Environment.EnvironmentName,
}));

app.MapGet("/api/dashboard", async (VirtualLabDbContext dbContext, IOptions<BenchmarkSeedProfile> profile) =>
{
    BenchmarkSeedProfile seed = profile.Value;
    int siteCount = await dbContext.Sites.CountAsync();
    int pumpCount = await dbContext.Pumps.CountAsync();
    int nozzleCount = await dbContext.Nozzles.CountAsync();
    int transactionCount = await dbContext.SimulatedTransactions.CountAsync();
    int callbackTargetCount = await dbContext.CallbackTargets.CountAsync();

    return Results.Ok(new
    {
        seed.ProfileName,
        SeedTargets = new
        {
            seed.Sites,
            Pumps = seed.TotalPumps,
            Nozzles = seed.TotalNozzles,
            seed.Transactions,
        },
        Current = new
        {
            Sites = siteCount,
            Pumps = pumpCount,
            Nozzles = nozzleCount,
            Transactions = transactionCount,
            CallbackTargets = callbackTargetCount,
        },
    });
});

app.MapGet("/api/sites", async (VirtualLabDbContext dbContext) =>
{
    var sites = await dbContext.Sites
        .AsNoTracking()
        .Include(site => site.ActiveFccSimulatorProfile)
        .Include(site => site.Pumps)
            .ThenInclude(pump => pump.Nozzles)
        .OrderBy(site => site.SiteCode)
        .Select(index => new
        {
            index.Id,
            index.SiteCode,
            index.Name,
            index.DeliveryMode,
            index.PreAuthMode,
            index.InboundAuthMode,
            ActiveProfile = index.ActiveFccSimulatorProfile.Name,
            Pumps = index.Pumps.Count,
            Nozzles = index.Pumps.SelectMany(pump => pump.Nozzles).Count(),
        })
        .ToListAsync();

    return Results.Ok(sites);
});

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

app.MapGet("/api/transactions", async (string? siteCode, string? correlationId, int? limit, VirtualLabDbContext dbContext) =>
{
    int take = Math.Clamp(limit ?? 100, 1, 100);
    IQueryable<VirtualLab.Domain.Models.SimulatedTransaction> query = dbContext.SimulatedTransactions
        .AsNoTracking()
        .Include(x => x.Site)
        .Include(x => x.Product)
        .OrderByDescending(x => x.OccurredAtUtc);

    if (!string.IsNullOrWhiteSpace(siteCode))
    {
        query = query.Where(x => x.Site.SiteCode == siteCode);
    }

    if (!string.IsNullOrWhiteSpace(correlationId))
    {
        query = query.Where(x => x.CorrelationId == correlationId);
    }

    var transactions = await query
        .Take(take)
        .Select(x => new
        {
            x.Id,
            x.ExternalTransactionId,
            x.CorrelationId,
            SiteCode = x.Site.SiteCode,
            PumpNumber = x.Pump.PumpNumber,
            NozzleNumber = x.Nozzle.NozzleNumber,
            ProductCode = x.Product.ProductCode,
            x.DeliveryMode,
            x.Status,
            x.Volume,
            x.TotalAmount,
            x.OccurredAtUtc,
            x.RawPayloadJson,
            x.CanonicalPayloadJson,
            x.MetadataJson,
            x.TimelineJson,
        })
        .ToListAsync();

    return Results.Ok(transactions);
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

app.MapPost("/api/sites/{siteId:guid}/transactions/push", async (
    Guid siteId,
    PushTransactionsRequest? request,
    IForecourtSimulationService forecourtSimulationService,
    CancellationToken cancellationToken) =>
{
    PushTransactionsResult result = await forecourtSimulationService.PushTransactionsAsync(siteId, request ?? new PushTransactionsRequest(), cancellationToken);
    return Results.Json(result, statusCode: result.StatusCode);
});

app.MapGet("/api/logs", async (string? category, string? siteCode, string? correlationId, int? limit, VirtualLabDbContext dbContext) =>
{
    int take = Math.Clamp(limit ?? 100, 1, 200);
    IQueryable<VirtualLab.Domain.Models.LabEventLog> query = dbContext.LabEventLogs
        .AsNoTracking()
        .Include(x => x.Site)
        .OrderByDescending(x => x.OccurredAtUtc);

    if (!string.IsNullOrWhiteSpace(category))
    {
        query = query.Where(x => x.Category == category);
    }

    if (!string.IsNullOrWhiteSpace(siteCode))
    {
        query = query.Where(x => x.Site != null && x.Site.SiteCode == siteCode);
    }

    if (!string.IsNullOrWhiteSpace(correlationId))
    {
        query = query.Where(x => x.CorrelationId == correlationId);
    }

    var logs = await query
        .Take(take)
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
            x.RawPayloadJson,
            x.CanonicalPayloadJson,
        })
        .ToListAsync();

    return Results.Ok(logs);
});

app.MapGet("/api/preauth-sessions", async (string? siteCode, string? correlationId, int? limit, IPreAuthSimulationService preAuthService, CancellationToken cancellationToken) =>
{
    int take = Math.Clamp(limit ?? 100, 1, 200);
    IReadOnlyList<PreAuthSessionSummary> sessions = await preAuthService.ListSessionsAsync(siteCode, correlationId, take, cancellationToken);
    return Results.Ok(sessions);
});

app.MapPost("/callbacks/{targetKey}", async (string targetKey, HttpContext httpContext, VirtualLabDbContext dbContext, CancellationToken cancellationToken) =>
{
    var target = await dbContext.CallbackTargets
        .AsNoTracking()
        .Where(x => x.TargetKey == targetKey && x.IsActive)
        .Select(x => new
        {
            x.Id,
            x.TargetKey,
            x.SiteId,
            ActiveProfileId = x.Site != null ? (Guid?)x.Site.ActiveFccSimulatorProfileId : null,
        })
        .SingleOrDefaultAsync(cancellationToken);

    if (target is null)
    {
        return Results.NotFound();
    }

    DateTimeOffset capturedAtUtc = DateTimeOffset.UtcNow;
    string requestBody = await ReadRequestBodyAsync(httpContext.Request, cancellationToken);
    Dictionary<string, string> sampleValues = ExtractSampleValues(requestBody);
    string correlationId = sampleValues.TryGetValue("correlationId", out string? bodyCorrelationId) && !string.IsNullOrWhiteSpace(bodyCorrelationId)
        ? bodyCorrelationId
        : InboundAuthRequestSanitizer.ResolveCorrelationId(httpContext);

    SimulatedTransaction? transaction = null;
    Guid? matchedAttemptId = null;

    if (httpContext.Request.Headers.TryGetValue("X-VirtualLab-Attempt-Id", out Microsoft.Extensions.Primitives.StringValues attemptHeaderValue) &&
        Guid.TryParse(attemptHeaderValue.ToString(), out Guid parsedAttemptId))
    {
        CallbackAttempt? matchedAttempt = await dbContext.CallbackAttempts
            .AsNoTracking()
            .Include(x => x.SimulatedTransaction)
            .SingleOrDefaultAsync(x => x.Id == parsedAttemptId && x.CallbackTargetId == target.Id, cancellationToken);

        if (matchedAttempt is not null)
        {
            matchedAttemptId = matchedAttempt.Id;
            transaction = matchedAttempt.SimulatedTransaction;
            correlationId = matchedAttempt.CorrelationId;
        }
    }

    if (!string.IsNullOrWhiteSpace(correlationId))
    {
        transaction = await dbContext.SimulatedTransactions
            .AsNoTracking()
            .Where(x => x.CorrelationId == correlationId)
            .OrderByDescending(x => x.OccurredAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    if (transaction is null &&
        sampleValues.TryGetValue("transactionId", out string? externalTransactionId) &&
        !string.IsNullOrWhiteSpace(externalTransactionId))
    {
        transaction = await dbContext.SimulatedTransactions
            .AsNoTracking()
            .Where(x => x.ExternalTransactionId == externalTransactionId)
            .OrderByDescending(x => x.OccurredAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    string responsePayload = JsonSerializer.Serialize(new
    {
        accepted = true,
        targetKey,
        capturedAtUtc,
    });

    if (transaction is not null && matchedAttemptId is null)
    {
        int nextAttemptNumber = await dbContext.CallbackAttempts
            .Where(x => x.CallbackTargetId == target.Id && x.SimulatedTransactionId == transaction.Id)
            .Select(x => (int?)x.AttemptNumber)
            .MaxAsync(cancellationToken) ?? 0;

        dbContext.CallbackAttempts.Add(new CallbackAttempt
        {
            Id = Guid.NewGuid(),
            CallbackTargetId = target.Id,
            SimulatedTransactionId = transaction.Id,
            CorrelationId = correlationId,
            AttemptNumber = nextAttemptNumber + 1,
            Status = CallbackAttemptStatus.Succeeded,
            ResponseStatusCode = StatusCodes.Status202Accepted,
            RequestUrl = httpContext.Request.Path.Value ?? $"/callbacks/{targetKey}",
            RequestHeadersJson = InboundAuthRequestSanitizer.SerializeHeaders(httpContext.Request.Headers),
            RequestPayloadJson = string.IsNullOrWhiteSpace(requestBody) ? "{}" : requestBody,
            ResponseHeadersJson = """{"content-type":"application/json"}""",
            ResponsePayloadJson = responsePayload,
            RetryCount = 0,
            MaxRetryCount = 0,
            AttemptedAtUtc = capturedAtUtc,
            CompletedAtUtc = capturedAtUtc,
            NextRetryAtUtc = null,
            AcknowledgedAtUtc = capturedAtUtc,
        });
    }

    dbContext.LabEventLogs.Add(new LabEventLog
    {
        Id = Guid.NewGuid(),
        SiteId = target.SiteId,
        FccSimulatorProfileId = target.ActiveProfileId,
        SimulatedTransactionId = transaction?.Id,
        CorrelationId = correlationId,
        Severity = "Information",
        Category = "CallbackAttempt",
        EventType = "CallbackCaptured",
        Message = $"Captured callback payload for target '{target.TargetKey}'.",
        RawPayloadJson = string.IsNullOrWhiteSpace(requestBody) ? "{}" : requestBody,
        CanonicalPayloadJson = transaction?.CanonicalPayloadJson ?? "{}",
        MetadataJson = JsonSerializer.Serialize(new
        {
            targetKey = target.TargetKey,
            matchedTransactionId = transaction?.ExternalTransactionId,
            linkedAttemptId = matchedAttemptId,
            storedAttempt = transaction is not null && matchedAttemptId is null,
            requestHeaders = JsonSerializer.Deserialize<object>(InboundAuthRequestSanitizer.SerializeHeaders(httpContext.Request.Headers)),
        }),
        OccurredAtUtc = capturedAtUtc,
    });

    await dbContext.SaveChangesAsync(cancellationToken);
    return Results.Accepted($"/api/callbacks/{targetKey}/history", JsonSerializer.Deserialize<object>(responsePayload));
});

app.MapGet("/api/callbacks/{targetKey}/history", async (string targetKey, VirtualLabDbContext dbContext) =>
{
    var attempts = await dbContext.CallbackAttempts
        .AsNoTracking()
        .Include(x => x.CallbackTarget)
        .Include(x => x.SimulatedTransaction)
        .Where(x => x.CallbackTarget.TargetKey == targetKey)
        .OrderByDescending(x => x.AttemptedAtUtc)
        .Select(x => new
        {
            x.Id,
            x.CorrelationId,
            x.AttemptNumber,
            x.Status,
            x.ResponseStatusCode,
            x.RequestUrl,
            x.RetryCount,
            x.MaxRetryCount,
            x.AttemptedAtUtc,
            x.CompletedAtUtc,
            x.NextRetryAtUtc,
            x.AcknowledgedAtUtc,
            x.RequestPayloadJson,
            x.ResponsePayloadJson,
            x.ErrorMessage,
            TransactionId = x.SimulatedTransaction.ExternalTransactionId,
        })
        .ToListAsync();

    return Results.Ok(attempts);
});

app.MapPost("/api/admin/seed", async (bool? reset, IVirtualLabSeedService seedService) =>
{
    await seedService.SeedAsync(reset ?? false);
    return Results.Accepted();
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
            nozzle = result.Nozzle,
            transaction = result.Transaction,
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

public partial class Program
{
}
