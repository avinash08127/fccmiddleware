using DPPMiddleware.Models;
using DppMiddleWareService.Models;

namespace DPPMiddleware.IRepository
{
    public interface ITransactionRepository
    {
        void InsertTransaction(TransactionEntity txn, DppMessage dppMessage);
        string InsertTransactions(TransactionEntity txn, DppMessage dppMessage,string masterResetKey);
        void HandlePriceSetRequest(string response);
        void InsertTransactionLogging(TransactionEntity txn);
        IEnumerable<SocketViewModel> GetByReferenceId(string referenceId);
        IEnumerable<FpEntity> GetAllFpStatusWithEvents(string? fpId = null, string? nozzleId = null, DateTime? createdDate = null, string? referenceId = null);
        IEnumerable<PumpTransactions> GetAllPumpTransactions();
        Task<List<PumpTransactions>> GetUnsyncedTransactionsAsync(string type, int? fpId, int? nozzleId, DateTime? createdDate, string? referenceId);
        Task UpdateOrderUuidAsync(string transactionId, string orderUuid, int orderId, string state);
        IEnumerable<FpSupTransBufStatusResponse> GetAllFpSupTransBufStatus();

        Task<IEnumerable<PumpTransactions>> GetLatestTransactionsAsync(int pumpId, int nozzleId, string emp);
        Task<IEnumerable<PumpTransactions>> GetAllLatestTransactionsAsync(object data);
        Task<IEnumerable<PumpTransactions>> GetAllTransactionsAsync();
        Task<bool> UpdateTransactionAsync(string transactionId, Dictionary<string, object> updateFields);
        Task AddTransactionAsync(object? transactionData);


        void UpdateFpStatusById(FpSatus fpStatus);

        Task<List<FuelPumpStatusDto>> GetAllFpStatus();
        // Task<List<AttendantLimit>> GetAttendantLimit();
        Task<IEnumerable<PumpTransactions>> GetAllTransactionsAsyncAttendant();
        Task<List<PumpTransactions>> GetOfflineTransactionsForSyncAsync();
        Task<bool> MarkTransactionsSyncedAsync(List<PumpTransactions> txns);
        Task<bool> UpdateAddToCartAsync(string transactionId, bool addToCart, string paymentId);

        Task<List<FpLimitDto>> GetTransactionLimitCountByFpId(int fpId);
        Task InsertBlockUnbloclHistory(int fpId, string actionType, string source, string note);
        Task FpLimitReset(int fpId);
        Task FpLimitReset(int fpId, int NewLimit);
        Task UpdateIsAllowedAsync(int fpId, bool isAllowed);
        Task<List<FpLimitDto?>> GetTransactionLimitCountByFpId_Block(int fpId);


        Task<bool> UpsertAttendantPumpCountAsync(AttendantPumpCountUpdate dto);
        Task<bool> UpdatePaymentIdAsync(string transactionId, string paymentId);
        Task UpdateIsDiscard(TransactionDiscard parameters);
    }
}
