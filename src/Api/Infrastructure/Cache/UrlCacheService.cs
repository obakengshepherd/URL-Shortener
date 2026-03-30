using System.Text.Json;
using StackExchange.Redis;

namespace UrlShortener.Infrastructure.Cache;

public class UrlCacheService
{
    private readonly IDatabase _db;
    private readonly ILogger<UrlCacheService> _logger;
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);

    public UrlCacheService(IConnectionMultiplexer redis, ILogger<UrlCacheService> logger)
    {
        _db     = redis.GetDatabase();
        _logger = logger;
    }

    public async Task<(string? OriginalUrl, DateTimeOffset? ExpiresAt, bool IsActive)?> GetAsync(string shortCode)
    {
        try
        {
            var val = await _db.StringGetAsync($"url:{shortCode}");
            if (!val.HasValue) return null;
            var record = JsonSerializer.Deserialize<CachedUrl>(val.ToString());
            if (record is null) return null;
            return (record.OriginalUrl, record.ExpiresAt, record.IsActive);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Redis URL cache read failed"); return null; }
    }

    public async Task SetAsync(string shortCode, string originalUrl, DateTimeOffset? expiresAt, bool isActive)
    {
        try
        {
            var ttl = expiresAt.HasValue
                ? new[] { DefaultTtl, expiresAt.Value - DateTimeOffset.UtcNow }.Min()
                : DefaultTtl;

            if (ttl <= TimeSpan.Zero) return; // already expired — don't cache

            await _db.StringSetAsync($"url:{shortCode}",
                JsonSerializer.Serialize(new CachedUrl(originalUrl, expiresAt, isActive)), ttl);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Redis URL cache write failed"); }
    }

    public async Task InvalidateAsync(string shortCode)
    {
        try { await _db.KeyDeleteAsync($"url:{shortCode}"); }
        catch (Exception ex) { _logger.LogWarning(ex, "Redis URL cache invalidation failed"); }
    }

    private record CachedUrl(string OriginalUrl, DateTimeOffset? ExpiresAt, bool IsActive);
}
