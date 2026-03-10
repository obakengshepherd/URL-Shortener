namespace UrlShortener.Application.Services;

using UrlShortener.Application.Interfaces;
using UrlShortener.Api.Models.Requests;
using UrlShortener.Api.Models.Responses;

public class UrlService : IUrlService
{
    public Task<UrlResponse> CreateUrlAsync(string userId, string? idempotencyKey, CreateUrlRequest request, CancellationToken ct) => throw new NotImplementedException("Implemented Day 15");
    public Task<UrlDeactivatedResponse> DeactivateAsync(string code, string userId, CancellationToken ct) => throw new NotImplementedException("Implemented Day 15");
    public Task<UrlResponse> UpdateAsync(string code, string userId, UpdateUrlRequest request, CancellationToken ct) => throw new NotImplementedException("Implemented Day 15");
}

public class RedirectService : IRedirectService
{
    public Task<RedirectResult> ResolveAsync(string shortCode, HttpContext context, CancellationToken ct) => throw new NotImplementedException("Implemented Day 15");
}

public class AnalyticsService : IAnalyticsService
{
    public Task<UrlStatsResponse> GetStatsAsync(string code, string userId, GetStatsRequest query, CancellationToken ct) => throw new NotImplementedException("Implemented Day 15");
}