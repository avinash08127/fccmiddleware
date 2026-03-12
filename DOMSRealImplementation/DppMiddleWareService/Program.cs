using DPPMiddleware.ForecourtTcpWorker;
using DPPMiddleware.Helpers;
using DPPMiddleware.Interface;
using DPPMiddleware.IRepository;
using DPPMiddleware.Models;
using DPPMiddleware.Repository;
using DPPMiddleware.Services;
using DppMiddleWareService;
using DppMiddleWareService.ForecourtTcpWorker;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);



Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(
        outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: "logs/forecourt-Server-log-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: null,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

// Integrate Serilog with the logging system
builder.Logging.ClearProviders();
builder.Logging.AddSerilog();


builder.Services.AddSingleton<AppDbContext>();

builder.Services.AddScoped<ITransactionRepository, TransactionRepository>();
builder.Services.AddScoped<ITransactionService, TransactionService>();
builder.Services.AddScoped<ILogRepository, LogRepository>();
builder.Services.AddScoped<ILoggingService, LoggingService>();
builder.Services.AddScoped<IParserService, ParserService>();
builder.Services.AddScoped<DppHexParser>();
builder.Services.AddScoped<DppMessageClassifier>();
builder.Services.AddScoped<Helper>();
builder.Services.AddSingleton<ForecourtClient>();
builder.Services.AddSingleton<PopupService>();

builder.Services.Configure<DPPMiddleware.ForecourtTcpWorker.WorkerOptions>(
builder.Configuration.GetSection("WorkerOptions"));
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<WebSocketServerHostedService>();
builder.Services.AddHostedService<PopupService>();

var host = builder.Build();




host.Run();



