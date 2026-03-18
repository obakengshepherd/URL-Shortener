using System.Text.Json;
using Dapper;
using Npgsql;
using RabbitMQ.Client;
using StackExchange.Redis;
using UrlShortener.Api.Models.Requests;
using UrlShortener.Api.Models.Responses;
using UrlShortener.Application.Interfaces;

namespace UrlShortener.Infrastructure.Persistence;

// ════════════════════════════════════════════════════════════════════════════
// URL REPOSITORY
// ════════════════════════════════════════════════════════════════════════════

public class UrlRepository
{
    private readonly string _connectionString;

    public UrlRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("PostgreSQL")
            ?? throw new InvalidOperationException("PostgreSQL connection string missing.");
    }

    public NpgsqlConnection CreateConnection() => new(_connectionString);

    public async Task<UrlRecord?> FindByCodeAsync(string shortCode)
    {
        using var conn = CreateConnection();
        const string sql = "SELECT * FROM urls WHERE short_code = @ShortCode";
        return await conn.QuerySingleOrDefaultAsync<UrlRecord>(sql, new { ShortCode = shortCode });
    }

    public async Task InsertAsync(UrlRecord url, NpgsqlConnection conn)
    {
        const string sql = """
            INSERT INTO urls (id, short_code, original_url, created_by, title, expires_at, is_active, click_count, created_at, updated_at)
            VALUES (@Id, @ShortCode, @OriginalUrl, @CreatedBy, @Title, @ExpiresAt, @IsActive, 0, @CreatedAt, @UpdatedAt)
            """;
        await conn.ExecuteAsync(sql, url);
    }

    public async Task<bool> InsertWithConflictCheckAsync(UrlRecord url)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        try
        {
            await InsertAsync(url, conn);
            return true;
        }
        catch (NpgsqlException ex) when (ex.SqlState == "23505") // unique_violation
        {
            return false; // collision
        }
    }

    public async Task<bool> AliasExistsAsync(string alias)
    {
        using var conn = CreateConnection();
        return await conn.QuerySingleAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM custom_aliases WHERE alias = @Alias OR EXISTS(SELECT 1 FROM urls WHERE short_code = @Alias))",
            new { Alias = alias });
    }

    public async Task InsertAliasAsync(string alias, string urlId, NpgsqlConnection conn)
    {
        const string sql = """
            INSERT INTO custom_aliases (alias, url_id, created_at)
            VALUES (@Alias, @UrlId, NOW())
            """;
        await conn.ExecuteAsync(sql, new { Alias = alias, UrlId = urlId });
    }

    public async Task UpdateAsync(UrlRecord url)
    {
        using var conn = CreateConnection();
        const string sql = """
            UPDATE urls SET title = @Title, expires_at = @ExpiresAt, is_active = @IsActive, updated_at = NOW()
            WHERE id = @Id
            """;
        await conn.ExecuteAsync(sql, url);
    }

    public async Task IncrementClickCountAsync(string urlId)
    {
        using var conn = CreateConnection();
        await conn.ExecuteAsync("UPDATE urls SET click_count = click_count + 1 WHERE id = @UrlId", new { UrlId = urlId });
    }

    public async Task<ClickStatsRecord> GetClickStatsAsync(string shortCode, DateTimeOffset from, DateTimeOffset to)
    {
        using var conn = CreateConnection();
        const string sql = """
            SELECT
                COUNT(*) AS total_clicks,
                COUNT(DISTINCT ip_hash) AS unique_clicks,
                clicked_at::date AS period,
                referer
            FROM url_clicks
            WHERE url_id = (SELECT id FROM urls WHERE short_code = @ShortCode)
              AND clicked_at BETWEEN @From AND @To
            GROUP BY clicked_at::date, referer
            ORDER BY period DESC
            """;
        var rows = (await conn.QueryAsync<dynamic>(sql, new { ShortCode = shortCode, From = from, To = to })).ToList();

        return new ClickStatsRecord
        {
            TotalClicks   = rows.Sum(r => (long)(r.total_clicks ?? 0)),
            UniqueClicks  = rows.Select(r => (string)(r.ip_hash ?? "")).Distinct().Count(),
            ClicksByPeriod = rows
                .GroupBy(r => (string)(r.period?.ToString("yyyy-MM-dd") ?? string.Empty))
                .Select(g => (Period: g.Key, Clicks: g.Sum(r => (long)(r.total_clicks ?? 0))))
                .ToList(),
            TopReferrers = rows
                .Where(r => r.referer is not null)
                .GroupBy(r => (string)r.referer)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => (Referrer: g.Key, Clicks: (long)g.Count()))
                .ToList()
        };
    }
}

public record UrlRecord
{
    public string Id { get; init; } = string.Empty;
    public string ShortCode { get; init; } = string.Empty;
    public string OriginalUrl { get; init; } = string.Empty;
    public string CreatedBy { get; init; } = string.Empty;
    public string? Title { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool IsActive { get; init; } = true;
    public long ClickCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public record ClickStatsRecord
{
    public long TotalClicks { get; init; }
    public int UniqueClicks { get; init; }
    public List<(string Period, long Clicks)> ClicksByPeriod { get; init; } = [];
    public List<(string Referrer, long Clicks)> TopReferrers { get; init; } = [];
}

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

namespace UrlShortener.Infrastructure.Messaging;

public class ClickEventPublisher : IDisposable
{
    private readonly IConnection? _connection;
    private readonly IModel? _channel;
    private const string Queue = "click.events";
    private readonly ILogger<ClickEventPublisher> _logger;

    public ClickEventPublisher(IConfiguration configuration, ILogger<ClickEventPublisher> logger)
    {
        _logger = logger;
        try
        {
            var factory = new ConnectionFactory
            {
                Uri = new Uri(configuration.GetConnectionString("RabbitMQ") ?? "amqp://devuser:devpass@localhost:5672/")
            };
            _connection = factory.CreateConnection();
            _channel    = _connection.CreateModel();
            _channel.QueueDeclare(queue: Queue, durable: true, exclusive: false, autoDelete: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RabbitMQ connection failed — click events will be dropped");
        }
    }

    public void PublishClick(string shortCode, string urlId, string? userAgent, string? referer, string? countryCode, string? ipHash)
    {
        if (_channel is null) return;
        try
        {
            var body = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                ShortCode   = shortCode,
                UrlId       = urlId,
                ClickedAt   = DateTimeOffset.UtcNow,
                UserAgent   = userAgent,
                Referer     = referer,
                CountryCode = countryCode,
                IpHash      = ipHash
            }));
            var props = _channel.CreateBasicProperties();
            props.Persistent = true;
            _channel.BasicPublish(exchange: "", routingKey: Queue, basicProperties: props, body: body);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Click event publish failed — non-fatal"); }
    }

    public void Dispose() { _channel?.Dispose(); _connection?.Dispose(); }
}

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

    // ── Code generation ───────────────────────────────────────────────────────

    /// <summary>
    /// Generates an 8-character base62 code, retrying on collision (up to 3 times).
    /// ~218 trillion combinations make collision probability negligible at low scale.
    /// At scale >100M URLs, switch to pre-generated code pool.
    /// </summary>
    private async Task<string> GenerateUniqueCodeAsync()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var code = GenerateBase62(8);
            if (await _repo.InsertWithConflictCheckAsync(new UrlRecord
                { Id = "tmp", ShortCode = code, OriginalUrl = "tmp", CreatedBy = "tmp",
                  CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow }))
            {
                // We need the actual record, so just return the code and let the caller insert
                return code;
            }
        }
        throw new CodeGenerationException("Failed to generate a unique short code after 3 attempts.");
    }

    private static string GenerateBase62(int length)
    {
        var random = new Random();
        return new string(Enumerable.Range(0, length)
            .Select(_ => Base62Chars[random.Next(Base62Chars.Length)])
            .ToArray());
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
// REDIRECT SERVICE — hot path
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
        // Step 1: Redis cache lookup
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

        // Step 2: Database fallback on cache miss
        var url = await _repo.FindByCodeAsync(shortCode);
        if (url is null)
            return new RedirectResult { Status = RedirectStatus.NotFound };

        // Populate cache for future requests
        await _cache.SetAsync(shortCode, url.OriginalUrl, url.ExpiresAt, url.IsActive);

        if (!url.IsActive || (url.ExpiresAt.HasValue && url.ExpiresAt.Value <= DateTimeOffset.UtcNow))
        {
            FireClickEvent(shortCode, url.Id, context, false);
            return new RedirectResult { Status = RedirectStatus.Gone };
        }

        FireClickEvent(shortCode, url.Id, context, true);
        return new RedirectResult { Status = RedirectStatus.Found, OriginalUrl = url.OriginalUrl };
    }

    /// <summary>
    /// Fire-and-forget click event. Never blocks the redirect response.
    /// </summary>
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
