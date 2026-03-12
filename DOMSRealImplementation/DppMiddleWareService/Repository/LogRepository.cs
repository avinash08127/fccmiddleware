using DPPMiddleware.IRepository;
using Microsoft.Data.SqlClient;
namespace DPPMiddleware.Repository
{
    public class LogRepository : ILogRepository
    {
        private readonly AppDbContext _context;

        public LogRepository(AppDbContext context)
        {
            _context = context;
        }

        public void InsertLog(string role, string? referenceId, string message, string logLevel)
        {
            using var conn = _context.CreateConnection();
            conn.Open();

            using var cmd = new SqlCommand("sp_InsertLog", conn);
            cmd.CommandType = System.Data.CommandType.StoredProcedure;

            cmd.Parameters.AddWithValue("@role", role);
            cmd.Parameters.AddWithValue("@referenceId", string.IsNullOrEmpty(referenceId) ? DBNull.Value : referenceId);
            cmd.Parameters.AddWithValue("@message", message);
            cmd.Parameters.AddWithValue("@logLevel", logLevel);

            cmd.ExecuteNonQuery();
        }
    }
}
