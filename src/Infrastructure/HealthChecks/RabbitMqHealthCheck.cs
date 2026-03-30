using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;

namespace Shared.Infrastructure.HealthChecks;

/// <summary>
/// RabbitMQ health check. Verifies AMQP connection for messaging availability.
/// Used by health check endpoints and deployment readiness gates.
/// </summary>
public class RabbitMqHealthCheck : IHealthCheck
{
    private readonly string _connectionString;

    public RabbitMqHealthCheck(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var factory = new ConnectionFactory { Uri = new Uri(_connectionString) };
            using var connection = factory.CreateConnection();
            using var channel = connection.CreateModel();
            channel.BasicQos(0, 1, false);
            return HealthCheckResult.Healthy("RabbitMQ connection is healthy.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded($"RabbitMQ check failed: {ex.Message}", ex);
        }
    }
}
