using FccMiddleware.Domain.Interfaces;
using FccMiddleware.Infrastructure.Events;
using FccMiddleware.Infrastructure.Persistence;
using FccMiddleware.Infrastructure.Workers;
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

    // ── Infrastructure: Event publisher ───────────────────────────────────────
    builder.Services.AddScoped<IEventPublisher, OutboxEventPublisher>();

    // ── Outbox Publisher Worker ───────────────────────────────────────────────
    builder.Services.Configure<OutboxWorkerOptions>(
        builder.Configuration.GetSection(OutboxWorkerOptions.SectionName));
    builder.Services.AddHostedService<OutboxPublisherWorker>();

    // ── Stale Transaction Detection Worker ──────────────────────────────────
    builder.Services.Configure<StaleTransactionWorkerOptions>(
        builder.Configuration.GetSection(StaleTransactionWorkerOptions.SectionName));
    builder.Services.AddHostedService<StaleTransactionWorker>();

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
