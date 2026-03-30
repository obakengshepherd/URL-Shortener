using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Shared.Api.Controllers;

/// <summary>
/// Standard health check response writer. Formats health check results as JSON.
/// </summary>
public static class HealthCheckEndpoint
{
    public static Task WriteResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var response = new
        {
            status = report.Status.ToString().ToLower(),
            duration_ms = report.TotalDuration.TotalMilliseconds,
            timestamp = DateTimeOffset.UtcNow,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString().ToLower(),
                duration_ms = e.Value.Duration.TotalMilliseconds,
                description = e.Value.Description,
                error = e.Value.Exception?.Message
            })
        };

        return context.Response.WriteAsJsonAsync(response);
    }
}
