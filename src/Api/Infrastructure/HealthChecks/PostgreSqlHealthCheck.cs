using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Shared.Infrastructure.HealthChecks;

/// <summary>
/// PostgreSQL health check. Executes a simple query to verify database connectivity.
/// Used by health check endpoints and deployment readiness gates.
/// </summary>
public class PostgreSqlHealthCheck : IHealthCheck
{
    private readonly string _connectionString;

    public PostgreSqlHealthCheck(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy("PostgreSQL connection is healthy.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"PostgreSQL check failed: {ex.Message}", ex);
        }
    }
}
