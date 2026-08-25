using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace QMgr.Infrastructure.Data;

public class DatabaseInitializer
{
    private readonly QMgrDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(
        QMgrDbContext context,
        IConfiguration configuration,
        ILogger<DatabaseInitializer> logger)
    {
        _context = context;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        try
        {
            var connectionString = _configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrEmpty(connectionString))
            {
                _logger.LogError("DefaultConnection connection string is not configured");
                throw new InvalidOperationException("DefaultConnection connection string is not configured");
            }

            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            var databaseName = builder.Database;

            if (string.IsNullOrEmpty(databaseName))
            {
                _logger.LogError("Database name is not specified in connection string");
                throw new InvalidOperationException("Database name is not specified in connection string");
            }

            // Check if database exists
            var databaseExists = await CheckDatabaseExistsAsync(builder, databaseName);

            if (!databaseExists)
            {
                _logger.LogInformation("Database '{DatabaseName}' does not exist. Creating...", databaseName);
                await CreateDatabaseAsync(builder, databaseName);
                _logger.LogInformation("Database '{DatabaseName}' created successfully", databaseName);
            }
            else
            {
                _logger.LogInformation("Database '{DatabaseName}' already exists", databaseName);
            }

            // Apply migrations
            _logger.LogInformation("Applying database migrations...");
            await _context.Database.MigrateAsync();
            _logger.LogInformation("Database migrations applied successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while initializing the database");
            throw;
        }
    }

    private async Task<bool> CheckDatabaseExistsAsync(NpgsqlConnectionStringBuilder builder, string databaseName)
    {
        // Connect to 'postgres' system database to check if our target database exists
        var tempBuilder = new NpgsqlConnectionStringBuilder(builder.ToString())
        {
            Database = "postgres"
        };

        await using var connection = new NpgsqlConnection(tempBuilder.ToString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM pg_database WHERE datname = @databaseName";
        command.Parameters.AddWithValue("@databaseName", databaseName);

        var result = await command.ExecuteScalarAsync();
        return result != null;
    }

    private async Task CreateDatabaseAsync(NpgsqlConnectionStringBuilder builder, string databaseName)
    {
        // Connect to 'postgres' system database to create our target database
        var tempBuilder = new NpgsqlConnectionStringBuilder(builder.ToString())
        {
            Database = "postgres"
        };

        await using var connection = new NpgsqlConnection(tempBuilder.ToString());
        await connection.OpenAsync();

        // Sanitize database name to prevent SQL injection
        // PostgreSQL identifiers are case-sensitive when quoted
        var safeDatabaseName = databaseName.Replace("\"", "\"\"");

        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{safeDatabaseName}\"";

        await command.ExecuteNonQueryAsync();
    }
}
