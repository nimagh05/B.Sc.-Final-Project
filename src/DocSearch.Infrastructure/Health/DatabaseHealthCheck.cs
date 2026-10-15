using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DocSearch.Infrastructure.Health;

public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly string? _connectionString;
    private readonly ILogger<DatabaseHealthCheck> _logger;

    public DatabaseHealthCheck(IConfiguration configuration, ILogger<DatabaseHealthCheck> logger)
    {
        _connectionString = configuration.GetConnectionString("Default");
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return HealthCheckResult.Unhealthy("Database connection string is not configured.");
        }

        try
        {
            // Build connection string with short connection timeout to avoid hanging health checks
            var builder = new NpgsqlConnectionStringBuilder(_connectionString)
            {
                Timeout = 3,
                CommandTimeout = 3
            };

            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new NpgsqlCommand("SELECT 1;", connection);
            await command.ExecuteScalarAsync(cancellationToken);

            return HealthCheckResult.Healthy("Database is reachable.");
        }
        catch (Exception ex)
        {
            // Structured log for operators, but sanitized response with zero secrets leaked to the caller
            _logger.LogError(ex, "Database readiness health check failed.");
            return HealthCheckResult.Unhealthy("Database connection failed or timed out.");
        }
    }
}
