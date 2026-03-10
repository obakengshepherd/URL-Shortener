namespace UrlShortener.Application.Interfaces;

using UrlShortener.Api.Models.Requests;
using UrlShortener.Api.Models.Responses;

/// <summary>
/// Manages URL creation (with base62 code generation), deactivation, and updates.
/// Performs write-through cache population on creation.
/// </summary>
public interface IUrlService
{
    Task<UrlResponse> CreateUrlAsync(string userId, string? idempotencyKey, CreateUrlRequest request, CancellationToken ct);
    Task<UrlDeactivatedResponse> DeactivateAsync(string code, string userId, CancellationToken ct);
    Task<UrlResponse> UpdateAsync(string code, string userId, UpdateUrlRequest request, CancellationToken ct);
}

/// <summary>
/// Hot-path redirect resolution.
/// Checks Redis cache → PostgreSQL fallback → fire-and-forget click event to RabbitMQ.
/// </summary>
public interface IRedirectService
{
    Task<RedirectResult> ResolveAsync(string shortCode, HttpContext context, CancellationToken ct);
}

/// <summary>Analytics reads — served from PostgreSQL read replica.</summary>
public interface IAnalyticsService
{
    Task<UrlStatsResponse> GetStatsAsync(string code, string userId, GetStatsRequest query, CancellationToken ct);
}