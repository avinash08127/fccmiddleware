using FccMiddleware.Domain.Interfaces;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Infrastructure.Adapters;
using FccMiddleware.Infrastructure.Events;
using FccMiddleware.Infrastructure.Persistence;
using FccMiddleware.Infrastructure.Repositories;
using FccMiddleware.Infrastructure.Workers;
using FccMiddleware.Adapter.Doms;
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

    builder.Services.AddScoped<ISiteFccConfigProvider, SiteFccConfigProvider>();
    builder.Services.AddHttpClient();
    builder.Services.AddSingleton<IFccAdapterFactory>(sp =>
        FccAdapterFactory.Create(registry =>
        {
            var hcf = sp.GetRequiredService<IHttpClientFactory>();
            registry[FccVendor.DOMS] = cfg =>
            {
                var client = hcf.CreateClient();
                if (!string.IsNullOrEmpty(cfg.HostAddress))
                {
                    client.BaseAddress = new Uri($"http://{cfg.HostAddress}:{cfg.Port}/api/v1/");
                    if (!string.IsNullOrEmpty(cfg.ApiKey))
                        client.DefaultRequestHeaders.Add("X-API-Key", cfg.ApiKey);
                }

                return new DomsCloudAdapter(client, cfg);
            };
        }));

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
