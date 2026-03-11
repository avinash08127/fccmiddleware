using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using VirtualLab.Application;
using VirtualLab.Application.Diagnostics;
using VirtualLab.Application.FccProfiles;
using VirtualLab.Domain.Benchmarking;
using VirtualLab.Infrastructure;
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

app.MapGet("/fcc/{siteCode}/health", async (string siteCode, IFccProfileService profileService, CancellationToken cancellationToken) =>
{
    ResolvedFccProfile? resolved = await profileService.ResolveBySiteCodeAsync(siteCode, cancellationToken);
    if (resolved is null)
    {
        return Results.NotFound();
    }

    try
    {
        FccProfilePreviewResult preview = await profileService.PreviewAsync(
            new FccProfilePreviewRequest(
                resolved.ProfileId,
                null,
                "health",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["siteCode"] = resolved.SiteCode,
                    ["profileKey"] = resolved.ProfileKey,
                }),
            cancellationToken);

        return Results.Content(preview.ResponseBody, preview.ResponseHeaders.TryGetValue("content-type", out string? contentType) ? contentType : "application/json");
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
});

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
            ProductCode = x.Product.ProductCode,
            x.DeliveryMode,
            x.Status,
            x.Volume,
            x.TotalAmount,
            x.OccurredAtUtc,
            x.RawPayloadJson,
            x.CanonicalPayloadJson,
        })
        .ToListAsync();

    return Results.Ok(transactions);
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
            x.AttemptedAtUtc,
            x.CompletedAtUtc,
            x.RequestPayloadJson,
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

app.MapGet("/fcc/{siteCode}/health", (string siteCode) => Results.Ok(new
{
    SiteCode = siteCode,
    Status = "Healthy",
    CheckedAt = DateTimeOffset.UtcNow,
}));

app.MapGet("/fcc/{siteCode}/transactions", (string siteCode, int? limit) =>
{
    int take = Math.Clamp(limit ?? 100, 1, 100);
    var transactions = Enumerable.Range(1, take)
        .Select(index => new
        {
            TransactionId = $"{siteCode}-TX-{index:D4}",
            Amount = 100 + index,
            DeliveredVia = index % 2 == 0 ? "PUSH" : "PULL",
        });

    return Results.Ok(transactions);
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
