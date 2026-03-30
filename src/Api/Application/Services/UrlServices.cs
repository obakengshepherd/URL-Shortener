using UrlShortener.Api.Models.Requests;
using UrlShortener.Api.Models.Responses;
using UrlShortener.Application.Interfaces;
using UrlShortener.Infrastructure.Cache;
using UrlShortener.Infrastructure.Messaging;
using UrlShortener.Infrastructure.Persistence;

namespace UrlShortener.Application.Services;

// ════════════════════════════════════════════════════════════════════════════
// URL SERVICE
// ════════════════════════════════════════════════════════════════════════════

public class UrlService : IUrlService
{
    private readonly UrlRepository _repo;
    private readonly UrlCacheService _cache;

    // Reserved short codes that cannot be used as custom aliases
    private static readonly HashSet<string> ReservedCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "api", "admin", "app", "www", "health", "docs", "help", "login",
        "logout", "signup", "register", "static", "assets", "favicon"
    };

    private const string Base62Chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    public UrlService(UrlRepository repo, UrlCacheService cache)
    {
        _repo  = repo;
        _cache = cache;
    }

    public async Task<UrlResponse> CreateUrlAsync(
        string userId, string? idempotencyKey,
        CreateUrlRequest request, CancellationToken ct)
    {
        if (request.ExpiresAt.HasValue && request.ExpiresAt.Value <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Expiry date must be in the future.");

        string shortCode;

        if (!string.IsNullOrEmpty(request.Alias))
        {
            // Validate custom alias
            if (ReservedCodes.Contains(request.Alias))
                throw new AliasReservedException(request.Alias);
            if (await _repo.AliasExistsAsync(request.Alias))
                throw new AliasConflictException(request.Alias);
            shortCode = request.Alias;
        }
        else
        {
            // Generate a unique base62 short code with collision retry
            shortCode = await GenerateUniqueCodeAsync();
        }

        var urlId = $"url_{Guid.NewGuid():N}";
        var record = new UrlRecord
        {
            Id          = urlId,
            ShortCode   = shortCode,
            OriginalUrl = request.OriginalUrl,
            CreatedBy   = userId,
            Title       = request.Title,
            ExpiresAt   = request.ExpiresAt,
            IsActive    = true,
            CreatedAt   = DateTimeOffset.UtcNow,
            UpdatedAt   = DateTimeOffset.UtcNow
        };

        await using var conn = _repo.CreateConnection();
        await conn.OpenAsync(ct);
        await _repo.InsertAsync(record, conn);

        if (!string.IsNullOrEmpty(request.Alias))
            await _repo.InsertAliasAsync(request.Alias, urlId, conn);

        // Write-through cache population
        await _cache.SetAsync(shortCode, request.OriginalUrl, request.ExpiresAt, true);

        return MapUrl(record);
    }

    public async Task<UrlDeactivatedResponse> DeactivateAsync(
        string code, string userId, CancellationToken ct)
    {
        var url = await _repo.FindByCodeAsync(code)
            ?? throw new UrlNotFoundException(code);

        if (url.CreatedBy != userId)
            throw new UnauthorizedAccessException($"URL '{code}' does not belong to user '{userId}'.");

        if (!url.IsActive)
            throw new InvalidOperationException($"URL '{code}' is already inactive.");

        var updated = url with { IsActive = false, UpdatedAt = DateTimeOffset.UtcNow };
        await _repo.UpdateAsync(updated);
        await _cache.InvalidateAsync(code);

        return new UrlDeactivatedResponse
        {
            ShortCode      = code,
            IsActive       = false,
            DeactivatedAt  = DateTimeOffset.UtcNow
        };
    }

    public async Task<UrlResponse> UpdateAsync(
        string code, string userId, UpdateUrlRequest request, CancellationToken ct)
    {
        var url = await _repo.FindByCodeAsync(code)
            ?? throw new UrlNotFoundException(code);

        if (url.CreatedBy != userId)
            throw new UnauthorizedAccessException($"URL '{code}' does not belong to user '{userId}'.");

        var updated = url with
        {
            Title     = request.Title ?? url.Title,
            ExpiresAt = request.ExpiresAt ?? url.ExpiresAt,
            IsActive  = request.IsActive ?? url.IsActive,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _repo.UpdateAsync(updated);
        await _cache.InvalidateAsync(code);
        await _cache.SetAsync(code, updated.OriginalUrl, updated.ExpiresAt, updated.IsActive);

        return MapUrl(updated);
    }

    private async Task<string> GenerateUniqueCodeAsync()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var code = GenerateBase62(8);
            var existing = await _repo.FindByCodeAsync(code);
            if (existing is null)
            {
                return code;
            }
        }
        throw new CodeGenerationException("Failed to generate a unique short code after 3 attempts.");
    }

    private static string GenerateBase62(int length)
    {
        var tokenData = new byte[length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(tokenData);
        var sb = new System.Text.StringBuilder(length);
        foreach (var b in tokenData)
        {
            sb.Append(Base62Chars[b % Base62Chars.Length]);
        }
        return sb.ToString();
    }

    private static UrlResponse MapUrl(UrlRecord r) => new()
    {
        Id          = r.Id,
        ShortCode   = r.ShortCode,
        ShortUrl    = $"https://go.short.internal/{r.ShortCode}",
        OriginalUrl = r.OriginalUrl,
        Title       = r.Title,
        ExpiresAt   = r.ExpiresAt,
        IsActive    = r.IsActive,
        CreatedAt   = r.CreatedAt
    };
}

// ════════════════════════════════════════════════════════════════════════════
// REDIRECT SERVICE
// ════════════════════════════════════════════════════════════════════════════

public class RedirectService : IRedirectService
{
    private readonly UrlRepository _repo;
    private readonly UrlCacheService _cache;
    private readonly ClickEventPublisher _clickPublisher;
    private readonly ILogger<RedirectService> _logger;

    public RedirectService(
        UrlRepository repo,
        UrlCacheService cache,
        ClickEventPublisher clickPublisher,
        ILogger<RedirectService> logger)
    {
        _repo           = repo;
        _cache          = cache;
        _clickPublisher = clickPublisher;
        _logger         = logger;
    }

    public async Task<RedirectResult> ResolveAsync(string shortCode, HttpContext context, CancellationToken ct)
    {
        var cached = await _cache.GetAsync(shortCode);
        if (cached.HasValue)
        {
            var (origUrl, expiresAt, isActive) = cached.Value;
            if (!isActive || (expiresAt.HasValue && expiresAt.Value <= DateTimeOffset.UtcNow))
            {
                FireClickEvent(shortCode, null, context, false);
                return new RedirectResult { Status = RedirectStatus.Gone };
            }
            FireClickEvent(shortCode, null, context, true);
            return new RedirectResult { Status = RedirectStatus.Found, OriginalUrl = origUrl };
        }

        var url = await _repo.FindByCodeAsync(shortCode);
        if (url is null)
            return new RedirectResult { Status = RedirectStatus.NotFound };

        await _cache.SetAsync(shortCode, url.OriginalUrl, url.ExpiresAt, url.IsActive);

        if (!url.IsActive || (url.ExpiresAt.HasValue && url.ExpiresAt.Value <= DateTimeOffset.UtcNow))
        {
            FireClickEvent(shortCode, url.Id, context, false);
            return new RedirectResult { Status = RedirectStatus.Gone };
        }

        FireClickEvent(shortCode, url.Id, context, true);
        return new RedirectResult { Status = RedirectStatus.Found, OriginalUrl = url.OriginalUrl };
    }

    private void FireClickEvent(string shortCode, string? urlId, HttpContext context, bool counted)
    {
        if (!counted || urlId is null) return;

        var ua      = context.Request.Headers.UserAgent.ToString();
        var referer = context.Request.Headers.Referer.ToString();
        var ip      = context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        var ipHash  = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(ip + DateTime.UtcNow.ToString("yyyy-MM-dd"))));

        _clickPublisher.PublishClick(shortCode, urlId, ua, referer, null, ipHash);
    }
}

// ════════════════════════════════════════════════════════════════════════════
// ANALYTICS SERVICE
// ════════════════════════════════════════════════════════════════════════════

public class AnalyticsService : IAnalyticsService
{
    private readonly UrlRepository _repo;

    public AnalyticsService(UrlRepository repo) => _repo = repo;

    public async Task<UrlStatsResponse> GetStatsAsync(
        string code, string userId, GetStatsRequest query, CancellationToken ct)
    {
        var url = await _repo.FindByCodeAsync(code)
            ?? throw new UrlNotFoundException(code);

        if (url.CreatedBy != userId)
            throw new UnauthorizedAccessException($"URL '{code}' does not belong to user '{userId}'.");

        var from = query.From is not null ? DateTimeOffset.Parse(query.From) : DateTimeOffset.UtcNow.AddDays(-30);
        var to   = query.To   is not null ? DateTimeOffset.Parse(query.To)   : DateTimeOffset.UtcNow;

        var stats = await _repo.GetClickStatsAsync(code, from, to);

        return new UrlStatsResponse
        {
            ShortCode      = code,
            TotalClicks    = stats.TotalClicks,
            UniqueClicks   = stats.UniqueClicks,
            ClicksByPeriod = stats.ClicksByPeriod.Select(p => new ClicksByPeriod
                { Period = p.Period, Clicks = p.Clicks }),
            TopReferrers   = stats.TopReferrers.Select(r => new ReferrerStat
                { Referrer = r.Referrer, Clicks = r.Clicks })
        };
    }
}

// ── URL exceptions ────────────────────────────────────────────────────────────

public class UrlNotFoundException(string code) : Exception($"Short URL '{code}' not found.");
public class AliasConflictException(string alias) : Exception($"Alias '{alias}' is already taken.");
public class AliasReservedException(string alias) : Exception($"Alias '{alias}' is a reserved word and cannot be used.");
public class CodeGenerationException(string message) : Exception(message);
