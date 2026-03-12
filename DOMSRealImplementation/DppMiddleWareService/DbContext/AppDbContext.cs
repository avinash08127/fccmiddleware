using Microsoft.Data.SqlClient;

public class AppDbContext
{
    private  string _connectionString;

    public AppDbContext(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
                            ?? throw new InvalidOperationException("Connection string not found.");
    }

    public SqlConnection CreateConnection()
    {
        return new SqlConnection(_connectionString);
    }
}
