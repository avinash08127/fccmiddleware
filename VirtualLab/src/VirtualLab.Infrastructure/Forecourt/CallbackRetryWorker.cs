using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VirtualLab.Infrastructure.Forecourt;

public sealed class CallbackRetryWorker(
    IServiceScopeFactory serviceScopeFactory,
    IOptions<CallbackDeliveryOptions> options,
    ILogger<CallbackRetryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = serviceScopeFactory.CreateAsyncScope();
                CallbackDeliveryService callbackDeliveryService = scope.ServiceProvider.GetRequiredService<CallbackDeliveryService>();
                int processed = await callbackDeliveryService.DispatchDueAttemptsAsync(stoppingToken);

                if (processed == 0)
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(Math.Max(options.Value.WorkerPollIntervalMs, 100)),
                        stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Callback retry worker failed while dispatching due attempts.");
                await Task.Delay(
                    TimeSpan.FromMilliseconds(Math.Max(options.Value.WorkerPollIntervalMs, 100)),
                    stoppingToken);
            }
        }
    }
}
