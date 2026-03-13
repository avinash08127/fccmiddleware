using FccMiddleware.Domain.Interfaces;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Application.Ingestion;
using FccMiddleware.Infrastructure.Adapters;
using FccMiddleware.Infrastructure.Events;
using FccMiddleware.Infrastructure.Observability;
using FccMiddleware.Infrastructure.Persistence;
using FccMiddleware.Infrastructure.Repositories;
using FccMiddleware.Infrastructure.Storage;
using FccMiddleware.Infrastructure.Workers;
using FccMiddleware.Application.Reconciliation;
using FccMiddleware.Application.Observability;
using FccMiddleware.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

// Bootstrap logger — active only until DI container is built.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    // Registers: Serilog (structured JSON → console), OpenTelemetry, base health check
    builder.AddServiceDefaults();

    builder.Services.AddMediatR(cfg =>
        cfg.RegisterServicesFromAssembly(typeof(FccMiddleware.Application.Common.Result<>).Assembly));

    // ── Infrastructure: Tenant context (null in worker = cross-tenant queries) ──
    builder.Services.AddScoped<TenantContext>();
    builder.Services.AddScoped<ICurrentTenantProvider>(
        sp => sp.GetRequiredService<TenantContext>());

    // ── Infrastructure: PostgreSQL (EF Core) ──────────────────────────────────
    builder.Services.AddDbContext<FccMiddlewareDbContext>((sp, opts) =>
        opts.UseNpgsql(
            sp.GetRequiredService<IConfiguration>().GetConnectionString("FccMiddleware")
            ?? string.Empty));
    builder.Services.AddScoped<IReconciliationDbContext>(sp => sp.GetRequiredService<FccMiddlewareDbContext>());
    builder.Services.AddSingleton<IPostgresPartitionManager, PostgresPartitionManager>();
    builder.Services.AddSingleton<IArchiveObjectStore, ArchiveObjectStore>();
    builder.Services.AddSingleton<IObservabilityMetrics, CloudWatchEmfMetricSink>();
    builder.Services.Configure<ReconciliationOptions>(
        builder.Configuration.GetSection(ReconciliationOptions.SectionName));
    builder.Services.AddScoped<ReconciliationMatchingService>();

    // ── Infrastructure: Event publisher ───────────────────────────────────────
    builder.Services.AddScoped<IEventPublisher, OutboxEventPublisher>();

    builder.Services.AddScoped<ISiteFccConfigProvider, SiteFccConfigProvider>();
    builder.Services.AddCloudFccAdapterFactory();

    // ── Outbox Publisher Worker ───────────────────────────────────────────────
    builder.Services.Configure<OutboxWorkerOptions>(
        builder.Configuration.GetSection(OutboxWorkerOptions.SectionName));
    builder.Services.AddHostedService<OutboxPublisherWorker>();

    // ── Stale Transaction Detection Worker ──────────────────────────────────
    builder.Services.Configure<StaleTransactionWorkerOptions>(
        builder.Configuration.GetSection(StaleTransactionWorkerOptions.SectionName));
    builder.Services.AddHostedService<StaleTransactionWorker>();

    builder.Services.Configure<PreAuthExpiryWorkerOptions>(
        builder.Configuration.GetSection(PreAuthExpiryWorkerOptions.SectionName));
    builder.Services.AddHostedService<PreAuthExpiryWorker>();

    builder.Services.Configure<UnmatchedReconciliationWorkerOptions>(
        builder.Configuration.GetSection(UnmatchedReconciliationWorkerOptions.SectionName));
    builder.Services.AddHostedService<UnmatchedReconciliationWorker>();

    builder.Services.Configure<MonitoringSnapshotWorkerOptions>(
        builder.Configuration.GetSection(MonitoringSnapshotWorkerOptions.SectionName));
    builder.Services.AddHostedService<MonitoringSnapshotWorker>();

    builder.Services.Configure<ArchiveWorkerOptions>(
        builder.Configuration.GetSection(ArchiveWorkerOptions.SectionName));
    builder.Services.AddHostedService<ArchiveWorker>();

    // ── OB-P03: Refresh Token Cleanup Worker ─────────────────────────────
    builder.Services.Configure<RefreshTokenCleanupWorkerOptions>(
        builder.Configuration.GetSection(RefreshTokenCleanupWorkerOptions.SectionName));
    builder.Services.AddHostedService<RefreshTokenCleanupWorker>();

    var host = builder.Build();
    host.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Worker terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
