namespace DPPMiddleware.Interface
{
    public interface ILoggingService
    {
        void Info(string role, string? referenceId, string message);
        void Error(string role, string? referenceId, string message);
    }
}
