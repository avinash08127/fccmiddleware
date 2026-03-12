using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VirtualLab.Application.PreAuth;

namespace VirtualLab.Infrastructure.PreAuth;

public sealed class PreAuthExpiryWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<PreAuthExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));

        while (!stoppingToken.IsCancellationRequested &&
               await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                IPreAuthSimulationService service = scope.ServiceProvider.GetRequiredService<IPreAuthSimulationService>();
                await service.ExpireSessionsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Pre-auth expiry scan failed.");
            }
        }
    }
}
