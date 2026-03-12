using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DppMiddleWareService.ForecourtTcpWorker
{
    public class OfflineSyncWorker : BackgroundService
    {
        private readonly ILogger<OfflineSyncWorker> _logger;
        private readonly IServiceProvider _services;

        public OfflineSyncWorker(ILogger<OfflineSyncWorker> logger, IServiceProvider services)
        {
            _logger = logger;
            _services = services;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("OfflineSyncWorker started at {time}", DateTime.Now);

            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("OfflineSyncWorker heartbeat at {time}", DateTime.Now);

                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }

            _logger.LogInformation("OfflineSyncWorker is stopping at {time}", DateTime.Now);
        }
    }
}
