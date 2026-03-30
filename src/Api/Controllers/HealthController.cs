using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Text.Json;

namespace Shared.Api.Controllers;

// ════════════════════════════════════════════════════════════════════════════
// HEALTH CONTROLLER
// ════════════════════════════════════════════════════════════════════════════
//
// Provides two health endpoints:
//
//   GET /health         — lightweight liveness probe (load balancer use)
//   GET /health/detail  — full readiness probe (deployment monitoring)
//
// Load balancer health check:
//   - Polls GET /health every 10 seconds
//   - Expects 200 OK within 2 seconds
//   - Removes instance from rotation after 3 consecutive failures
//   - Restores after 2 consecutive successes
//
// The /health endpoint is intentionally lightweight — no database queries,
// no network calls. It answers "is this process alive and able to serve requests?"
//
// The /health/detail endpoint checks all dependencies. Use for:
//   - Deployment readiness gates (don't route traffic until all checks pass)
//   - Monitoring dashboards
//   - Post-deployment smoke tests
// ════════════════════════════════════════════════════════════════════════════

[ApiController]
[Route("health")]
public class HealthController : ControllerBase
{
    private readonly HealthCheckService _healthCheckService;

    public HealthController(HealthCheckService healthCheckService)
    {
        _healthCheckService = healthCheckService;
    }

    /// <summary>
    /// Lightweight liveness probe. Returns 200 immediately if the process is alive.
    /// Used by load balancers for routing decisions.
    /// Does NOT check database or cache connectivity — just process liveness.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(200)]
    [ProducesResponseType(503)]
    public IActionResult Liveness()
    {
        return Ok(new
        {
            status    = "healthy",
            timestamp = DateTimeOffset.UtcNow,
            version   = GetType().Assembly.GetName().Version?.ToString() ?? "1.0.0"
        });
    }

    /// <summary>
    /// Full readiness probe. Checks all dependencies (PostgreSQL, Redis, Kafka/RabbitMQ).
    /// Returns 200 if all checks pass, 503 if any check fails or is degraded.
    /// Used by deployment pipelines and monitoring systems.
    /// </summary>
    [HttpGet("detail")]
    [ProducesResponseType(200)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> Readiness(CancellationToken ct)
    {
        var report = await _healthCheckService.CheckHealthAsync(ct);

        var response = new
        {
            status      = report.Status.ToString().ToLower(),
            duration_ms = report.TotalDuration.TotalMilliseconds,
            timestamp   = DateTimeOffset.UtcNow,
            checks      = report.Entries.Select(e => new
            {
                name        = e.Key,
                status      = e.Value.Status.ToString().ToLower(),
                duration_ms = e.Value.Duration.TotalMilliseconds,
                description = e.Value.Description,
                error       = e.Value.Exception?.Message
            })
        };

        return report.Status == HealthStatus.Healthy
            ? Ok(response)
            : StatusCode(StatusCodes.Status503ServiceUnavailable, response);
    }
}

// ════════════════════════════════════════════════════════════════════════════
// HEALTH CHECK REGISTRATION — shared extension method for all systems
// ════════════════════════════════════════════════════════════════════════════

public static class HealthCheckExtensions
{
    /// <summary>
    /// Registers health check endpoints with the standard configuration:
    ///   GET /health        → liveness (always 200 if process alive)
    ///   GET /health/detail → readiness (checks all dependencies)
    /// </summary>
    public static WebApplication MapHealthEndpoints(this WebApplication app)
    {
        // Minimal liveness probe — for load balancers
        // Returns 200 even if DB is down — signals "process is alive, keep routing"
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate         = _ => false,   // no checks — pure liveness
            ResponseWriter    = WriteHealthResponse,
            ResultStatusCodes = { [HealthStatus.Healthy] = 200 }
        });

        // Full readiness probe — for deployment gates
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate      = _ => true,        // all registered checks
            ResponseWriter = WriteHealthResponse,
            ResultStatusCodes =
            {
                [HealthStatus.Healthy]   = 200,
                [HealthStatus.Degraded]  = 200,  // degraded = still serving (Redis miss = slower, not broken)
                [HealthStatus.Unhealthy] = 503
            }
        });

        // Simple /health for backwards compatibility
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            Predicate         = _ => false,
            ResponseWriter    = WriteHealthResponse,
            ResultStatusCodes = { [HealthStatus.Healthy] = 200 }
        });

        return app;
    }

    private static async Task WriteHealthResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var response = new
        {
            status    = report.Status.ToString().ToLower(),
            timestamp = DateTimeOffset.UtcNow,
            checks    = report.Entries.Select(e => new
            {
                name        = e.Key,
                status      = e.Value.Status.ToString().ToLower(),
                duration_ms = Math.Round(e.Value.Duration.TotalMilliseconds, 2),
                description = e.Value.Description
            })
        };
        await context.Response.WriteAsJsonAsync(response);
    }
}
