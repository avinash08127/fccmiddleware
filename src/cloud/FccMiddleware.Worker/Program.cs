using FccMiddleware.ServiceDefaults;
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

    // TODO CB-1.x: Register background IHostedService workers here, e.g.:
    // builder.Services.AddHostedService<ReconciliationWorker>();

    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Worker terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
