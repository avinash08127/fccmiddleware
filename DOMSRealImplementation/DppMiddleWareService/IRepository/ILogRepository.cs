namespace DPPMiddleware.IRepository
{
    public interface ILogRepository
    {
        void InsertLog(string role, string? referenceId, string message, string logLevel);
    }
}
