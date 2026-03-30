using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Shared.Infrastructure.HealthChecks;

/// <summary>
/// Redis health check. Executes PING command to verify cache connectivity.
/// Used by health check endpoints and deployment readiness gates.
/// </summary>
public class RedisHealthCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer _redis;

    public RedisHealthCheck(IConnectionMultiplexer redis)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var ping = await db.ExecuteAsync("PING");
            return HealthCheckResult.Healthy("Redis connection is healthy.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded($"Redis check failed: {ex.Message}", ex);
        }
    }
}
