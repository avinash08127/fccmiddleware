using DPPMiddleware.IRepository;
using DPPMiddleware.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DppMiddleWareService.ForecourtTcpWorker
{
    public class OdduSyncWorker : BackgroundService
    {
        private readonly ILogger<OdduSyncWorker> _logger;
        private readonly IServiceProvider _services;

        public OdduSyncWorker(ILogger<OdduSyncWorker> logger, IServiceProvider services)
        {
            _logger = logger;
            _services = services;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("OfflineSyncWorker started at {time}", DateTime.Now);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _services.CreateScope();
                    var repo = scope.ServiceProvider.GetRequiredService<ITransactionRepository>();

                    var transactions = repo.GetAllFpSupTransBufStatus()
                                           .Where(t => t.TransInSupBuffer.Any()) 
                                           .ToList();

                    string json = string.Empty;
                    if (transactions != null)
                    {
                        json = JsonSerializer.Serialize(transactions, new JsonSerializerOptions { WriteIndented = true });
                        _logger.LogInformation("Success fetching transaction {json}", json);
                        break;

                    }
                    else
                    {
                        _logger.LogWarning("Error fetching transaction");
                        break;
                    }

                    _logger.LogInformation("OfflineSyncWorker heartbeat at {time}", DateTime.Now);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in OfflineSyncWorker loop");
                }

                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }

            _logger.LogInformation("OfflineSyncWorker is stopping at {time}", DateTime.Now);
        }
    }
}
