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
