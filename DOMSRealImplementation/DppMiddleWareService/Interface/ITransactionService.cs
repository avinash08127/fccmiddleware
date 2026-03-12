using DPPMiddleware.Models;
using DppMiddleWareService.Models;
using Fleck;
using System.Net.WebSockets;

namespace DPPMiddleware.Interface
{
    public interface ITransactionService
    {
        Task HandleAttendantSocket(WebSocket socket, string filterParams);
        Task HandleSiteManagerSocket(WebSocket socket, string filterParams);
        Task HandleWebSocketRequest(IWebSocketConnection _connection, string? type, int? fpId, int? nozzleId, DateTime? createdDate, string? referenceId);
        Task UpdateOrderUuidAsync(string transactionId, string orderUuid, int OrderId, string State);

        Task<IEnumerable<object>> GetLatestTransactions(int pumpId, int nozzleId, string emp);
        Task<IEnumerable<object>> GetAllTransactions();
        Task<IEnumerable<object>> GetAllLatestTransactions(object data);
        Task<IEnumerable<object>> GetAllTransactionsAttendant();
        Task<bool> UpdateTransaction(string transactionId, Dictionary<string, object> updateFields);
        Task AddTransaction(object? transactionData);
        Task<List<FuelPumpStatusDto>> GetAllFpStatus();
        //Task<List<AttendantLimit>> GetAttendantLimit();
        Task<List<PumpTransactions>> SyncOfflineTransactionsAsync();
        Task<bool> MarkTransactionsSyncedAsync(List<PumpTransactions> txns);
        Task FpLimitReset(int fpId);
        Task<List<FpLimitDto>> GetTransactionLimitCountByFpId(int fpId);

        Task<bool> UpdateAddToCartAsync(string transactionId, bool addToCart, string paymentId);

        Task<bool> UpdateAttendantPumpCountAsync(AttendantPumpCountUpdate dto);
        Task<bool> UpdatePaymentIdAsync(string transactionId, string paymentId);
        Task UpdateIsDiscard(TransactionDiscard data);

    }
}
