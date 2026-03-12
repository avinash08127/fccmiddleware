using DPPMiddleware.Interface;
using DPPMiddleware.IRepository;
using DPPMiddleware.Models;
using DppMiddleWareService.Models;
using Fleck;
using System.Net.WebSockets;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DPPMiddleware.Services
{
    public class TransactionService : ITransactionService
    {
        private readonly ITransactionRepository _transactionRepo;
        private readonly ILoggingService _logging;

        public TransactionService(ITransactionRepository transactionRepo, ILoggingService logging)
        {
            _transactionRepo = transactionRepo;
            _logging = logging;
        }
        public async Task HandleAttendantSocket(WebSocket socket, string filterParams)
        {
            _logging.Info("Attendant", null, "Connected");

            // Parse query string filters
            var query = System.Web.HttpUtility.ParseQueryString(filterParams);
            string? referenceId = query["ReferenceId"];
            string? fpId = query["FpId"];
            string? nozzleId = query["NozzleId"];
            DateTime? createdDate = DateTime.TryParse(query["CreatedDate"], out var d) ? d : null;

            _logging.Info("Attendant", null, $"Filters: ReferenceId={referenceId}, FpId={fpId}, NozzleId={nozzleId}, CreatedDate={createdDate}");

            // Fetch filtered transactions
            var txns = _transactionRepo.GetAllFpStatusWithEvents(fpId, nozzleId, createdDate, referenceId);

            // Build response
            var responseObj = new
            {
                role = "Attendant",
                status = txns.Any() ? "success" : "not_found",
                referenceId,
                transactions = txns
            };

            var jsonResponse = JsonSerializer.Serialize(responseObj, new JsonSerializerOptions { WriteIndented = true });
            await socket.SendAsync(Encoding.UTF8.GetBytes(jsonResponse), WebSocketMessageType.Text, true, CancellationToken.None);

            _logging.Info("Attendant", referenceId, $"Sent {txns.Count()} records");

            // await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Completed", CancellationToken.None);
        }

        public async Task HandleSiteManagerSocket(WebSocket socket, string filterParams)
        {
            _logging.Info("SiteManager", null, "Connected");

            var query = System.Web.HttpUtility.ParseQueryString(filterParams);
            string? fpId = query["FpId"];
            string? nozzleId = query["NozzleId"];
            DateTime? createdDate = DateTime.TryParse(query["CreatedDate"], out var d) ? d : null;
            string? referenceId = query["ReferenceId"];

            _logging.Info("SiteManager", null, $"Filters: FpId={fpId}, NozzleId={nozzleId}, CreatedDate={createdDate}, ReferenceId={referenceId}");

            var txns = _transactionRepo.GetAllFpStatusWithEvents(fpId, nozzleId, createdDate, referenceId);

            var responseObj = new
            {
                role = "SiteManager",
                status = txns.Any() ? "success" : "not_found",
                transactions = txns
            };

            var jsonResponse = JsonSerializer.Serialize(responseObj, new JsonSerializerOptions { WriteIndented = true });
            await socket.SendAsync(Encoding.UTF8.GetBytes(jsonResponse), WebSocketMessageType.Text, true, CancellationToken.None);

            _logging.Info("SiteManager", null, $"Sent {txns.Count()} records");
            // await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Completed", CancellationToken.None);
        }

        public async Task HandleWebSocketRequest(IWebSocketConnection _connection, string? type, int? fpId, int? nozzleId, DateTime? createdDate, string? referenceId)
        {
            try
            {

                List<PumpTransactions> unsyncedTransactions = await _transactionRepo.GetUnsyncedTransactionsAsync(type, fpId, nozzleId, createdDate, referenceId);

                if (unsyncedTransactions != null && unsyncedTransactions.Any())
                {
                    var response = new
                    {
                        // type = type ?? "unsynced",
                        type = "latest",
                        // status = "success",
                        data = unsyncedTransactions
                    };

                    var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = null,
                        WriteIndented = false,
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                    });

                    await _connection.Send(json);
                    Console.WriteLine($"📤 Sent response: {json}");
                }
                else
                {
                    var response = new
                    {
                        // type = type ?? "unsynced",
                        type = "latest",
                        // status = "success",
                        data = unsyncedTransactions
                    };

                    var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = null,
                        WriteIndented = false,
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                    });
                    await _connection.Send(json);
                    Console.WriteLine("ℹ️ No unsynced transactions found.");

                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error processing WebSocket request: {ex.Message}");
                await SendError(_connection, "Error processing request");
            }
        }

        public async Task UpdateOrderUuidAsync(string transactionId, string orderUuid, int OrderId, string state)
        {
            await _transactionRepo.UpdateOrderUuidAsync(transactionId, orderUuid, OrderId, state);
        }

        public async Task UpdateIsDiscard(TransactionDiscard data)
        {
            await _transactionRepo.UpdateIsDiscard(data);
        }
        public async Task FpLimitReset(int fpId)
        {
            //  _logging.Info("TransactionService", null, $"Adding new transaction: {transactionData}");
            await _transactionRepo.FpLimitReset(fpId);
        }
        public async Task<List<FpLimitDto>> GetTransactionLimitCountByFpId(int fpId)
        {
            return await _transactionRepo.GetTransactionLimitCountByFpId(fpId);
        }
        public async Task<bool> UpdateAddToCartAsync(string transactionId, bool addToCart, string paymentId)
        {
            return await _transactionRepo.UpdateAddToCartAsync(transactionId, addToCart, paymentId);
        }

        public async Task<bool> UpdatePaymentIdAsync(string transactionId, string paymentId)
        {
            if (string.IsNullOrEmpty(transactionId) || string.IsNullOrEmpty(paymentId))
                return false;

            return await _transactionRepo.UpdatePaymentIdAsync(transactionId, paymentId);
        }


        #region Transaction CRUD
        public async Task<IEnumerable<object>> GetLatestTransactions(int pumpId, int nozzleId, string emp)
        {
            _logging.Info("TransactionService", null, $"Fetching latest transactions for Pump={pumpId}, Nozzle={nozzleId}, Emp={emp}");
            var txns = await _transactionRepo.GetLatestTransactionsAsync(pumpId, nozzleId, emp);
            return txns?.Cast<object>() ?? Enumerable.Empty<object>();
        }

        public async Task<IEnumerable<object>> GetAllLatestTransactions(object data)
        {
            _logging.Info("TransactionService", null, "Fetching all transactions");
            var txns = await _transactionRepo.GetAllLatestTransactionsAsync(data);
            return txns?.Cast<object>() ?? Enumerable.Empty<object>();
        }
        public async Task<IEnumerable<object>> GetAllTransactions()
        {
            _logging.Info("TransactionService", null, "Fetching all transactions");
            var txns = await _transactionRepo.GetAllTransactionsAsync();
            return txns?.Cast<object>() ?? Enumerable.Empty<object>();
        }

        public async Task<IEnumerable<object>> GetAllTransactionsAttendant()
        {
            _logging.Info("TransactionService", null, "Fetching all transactions");
            var txns = await _transactionRepo.GetAllTransactionsAsyncAttendant();
            return txns?.Cast<object>() ?? Enumerable.Empty<object>();
        }

        public Task<List<FuelPumpStatusDto>> GetAllFpStatus()
        {
            _logging.Info("GetAllFpStatus", null, "Fetching latest Fp Status for Pumps");

            return _transactionRepo.GetAllFpStatus();
        }
         
        public async Task<bool> UpdateTransaction(string transactionId, Dictionary<string, object> updateFields)
        {
            _logging.Info("TransactionService", transactionId, $"Updating transaction with fields: {JsonSerializer.Serialize(updateFields)}");
            return await _transactionRepo.UpdateTransactionAsync(transactionId, updateFields);
        }

        public async Task<List<PumpTransactions>> SyncOfflineTransactionsAsync()
        {
            try
            {
                // 1️⃣ Fetch offline transactions
                var offlineTransactions = await _transactionRepo.GetOfflineTransactionsForSyncAsync();

                if (offlineTransactions == null || offlineTransactions.Count == 0)
                {
                    _logging.Info("TransactionService", "Sync", "No offline transactions found.");
                    return new List<PumpTransactions>();
                }

                _logging.Info("TransactionService", "Sync", $"Found {offlineTransactions.Count} offline transactions.");

                // 2️⃣ Mark them as synced
                bool updated = await _transactionRepo.MarkTransactionsSyncedAsync(offlineTransactions);

                if (!updated)
                {
                    _logging.Info("TransactionService", "Sync", "Failed to update sync status for offline transactions.");
                }
                else
                {
                    _logging.Info("TransactionService", "Sync", "Offline transactions marked as synced.");
                }

                // 3️⃣ Return the offline list to caller (frontend)
                return offlineTransactions;
            }
            catch (Exception ex)
            {
                _logging.Error("TransactionService", "Sync", $"Error syncing offline transactions: {ex.Message}");
                throw;
            }
        }

        public async Task<bool> MarkTransactionsSyncedAsync(List<PumpTransactions> txns)
        {
            if (txns == null || txns.Count == 0)
                return false;

            return await _transactionRepo.MarkTransactionsSyncedAsync(txns);
        }

        public async Task<bool> UpdateAttendantPumpCountAsync(AttendantPumpCountUpdate dto)
        {
            if (dto == null || dto.PumpNumber <= 0)
                return false;

            return await _transactionRepo.UpsertAttendantPumpCountAsync(dto);
        }


        public async Task AddTransaction(object? transactionData)
        {
            _logging.Info("TransactionService", null, $"Adding new transaction: {transactionData}");
            await _transactionRepo.AddTransactionAsync(transactionData);
        }
        #endregion


        private async Task SendError(IWebSocketConnection _connection, string message)
        {
            var error = JsonSerializer.Serialize(new { status = "error", message });
            await _connection.Send(error);
        }
    }

}
