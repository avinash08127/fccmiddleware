using DPPMiddleware.Interface;
using DPPMiddleware.IRepository;

namespace DPPMiddleware.Services
{
    public class LoggingService : ILoggingService
    {
        private readonly ILogRepository _logRepo;

        public LoggingService(ILogRepository logRepo)
        {
            _logRepo = logRepo;
        }

        public void Info(string role, string? referenceId, string message)
        {
            _logRepo.InsertLog(role, referenceId, message, "Info");
            Console.WriteLine($"[INFO] {role} - {message}");
        }

        public void Error(string role, string? referenceId, string message)
        {
            _logRepo.InsertLog(role, referenceId, message, "Error");
            Console.WriteLine($"[ERROR] {role} - {message}");
        }
    }
}
