using System.Net;
using StackExchange.Redis;

namespace Shared.Infrastructure.RateLimit;

// ════════════════════════════════════════════════════════════════════════════
// DISTRIBUTED SLIDING WINDOW RATE LIMITER
// ════════════════════════════════════════════════════════════════════════════
//
// Implements a sliding window counter using Redis INCR + EXPIRE.
//
// Why sliding window over fixed window?
//   Fixed window: 100 req/min resets at :00 every minute. A burst of 100 at
//   :59 and 100 at :01 results in 200 requests hitting the backend in 2 seconds.
//
//   Sliding window: counts requests in the last N seconds regardless of clock
//   alignment. A burst of 100 at :59 blocks all requests until :59+window.
//   More accurate at the cost of slightly more Redis operations.
//
// Why Redis over in-process rate limiting?
//   In-process limiters (ASP.NET's built-in AddRateLimiter) are per-instance.
//   With 3 API instances, a user could make 3x the intended limit — once per
//   instance. Redis provides a shared counter across all instances, enforcing
//   the limit correctly in a horizontally scaled deployment.
//
// Implementation: INCR + EXPIRE (approximation of sliding window)
//   1. INCR {key}         — atomic increment
//   2. If count == 1: EXPIRE {key} {window}  — set TTL on first request
//   3. If count > limit:  reject with 429
//
// This is an approximation because the window starts from the first request
// in the window, not rolling from the current second. At the window boundary
// it can allow up to 2x the limit briefly (same as fixed window). For most
// APIs this is acceptable — a true sliding window requires a sorted set and
// is more expensive.
//
// True sliding window (sorted set) is also provided below for high-security
// endpoints (POST /transfer, POST /payments).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Configuration for a rate limit rule.
/// </summary>
public record RateLimitRule
{
    /// <summary>Human-readable policy name, e.g. "authenticated", "transfer-writes"</summary>
    public string PolicyName { get; init; } = string.Empty;

    /// <summary>Maximum requests allowed in the window.</summary>
    public int Limit { get; init; }

    /// <summary>Window duration. Shorter = more precise, more Redis ops.</summary>
    public TimeSpan Window { get; init; }

    /// <summary>HTTP paths this rule applies to. Null = all paths.</summary>
    public IEnumerable<string>? Paths { get; init; }

    /// <summary>HTTP methods this rule applies to. Null = all methods.</summary>
    public IEnumerable<string>? Methods { get; init; }
}

/// <summary>
/// Redis-backed distributed sliding window rate limiter middleware.
///
/// Applies rate limits per user (from JWT) or per IP (for unauthenticated).
/// Returns standard X-RateLimit-* headers on every response.
/// Returns 429 with Retry-After when limit is exceeded.
///
/// Register: app.UseMiddleware&lt;RedisRateLimitMiddleware&gt;();
/// Place AFTER: app.UseAuthentication(); app.UseAuthorization();
/// </summary>
public class RedisRateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IDatabase _redis;
    private readonly IEnumerable<RateLimitRule> _rules;
    private readonly ILogger<RedisRateLimitMiddleware> _logger;

    public RedisRateLimitMiddleware(
        RequestDelegate next,
        IConnectionMultiplexer redis,
        IEnumerable<RateLimitRule> rules,
        ILogger<RedisRateLimitMiddleware> logger)
    {
        _next   = next;
        _redis  = redis.GetDatabase();
        _rules  = rules;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var rule = MatchRule(context);
        if (rule is null)
        {
            await _next(context);
            return;
        }

        var identifier = GetIdentifier(context);
        var key        = $"rl:{rule.PolicyName}:{identifier}";

        var (allowed, count, resetSeconds) = await CheckLimitAsync(key, rule);

        // Always set rate limit headers, even when allowed
        SetRateLimitHeaders(context, rule.Limit, count, resetSeconds);

        if (!allowed)
        {
            _logger.LogWarning(
                "Rate limit exceeded: policy={Policy}, identifier={Identifier}, count={Count}, limit={Limit}",
                rule.PolicyName, identifier, count, rule.Limit);

            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers["Retry-After"] = resetSeconds.ToString();
            context.Response.ContentType = "application/json";

            await context.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    code    = "RATE_LIMIT_EXCEEDED",
                    message = $"Rate limit exceeded. Maximum {rule.Limit} requests per {rule.Window.TotalSeconds}s window.",
                    details = new[] { new { field = "requests", issue = $"Retry after {resetSeconds} seconds." } }
                },
                meta = new { request_id = context.TraceIdentifier, timestamp = DateTimeOffset.UtcNow }
            });
            return;
        }

        await _next(context);
    }

    // ── Sliding window check using INCR + EXPIRE ──────────────────────────────

    private async Task<(bool Allowed, long Count, long ResetSeconds)> CheckLimitAsync(
        string key, RateLimitRule rule)
    {
        try
        {
            var count = await _redis.StringIncrementAsync(key);

            if (count == 1)
            {
                // First request in this window — set the TTL
                await _redis.KeyExpireAsync(key, rule.Window);
            }

            var ttl          = await _redis.KeyTimeToLiveAsync(key);
            var resetSeconds = (long)(ttl?.TotalSeconds ?? rule.Window.TotalSeconds);

            return (count <= rule.Limit, count, resetSeconds);
        }
        catch (Exception ex)
        {
            // Redis unavailable — fail open (allow request) to avoid service disruption
            _logger.LogWarning(ex, "Redis rate limit check failed — allowing request (fail open)");
            return (true, 0, (long)rule.Window.TotalSeconds);
        }
    }

    // ── Rule matching ─────────────────────────────────────────────────────────

    private RateLimitRule? MatchRule(HttpContext context)
    {
        var path   = context.Request.Path.Value ?? string.Empty;
        var method = context.Request.Method;

        // Most specific rule wins — iterate in order (most restrictive first)
        foreach (var rule in _rules)
        {
            var pathMatches   = rule.Paths   is null || rule.Paths.Any(p   => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            var methodMatches = rule.Methods is null || rule.Methods.Any(m => m.Equals(method, StringComparison.OrdinalIgnoreCase));
            if (pathMatches && methodMatches) return rule;
        }
        return null;
    }

    // ── Identifier extraction ─────────────────────────────────────────────────

    private static string GetIdentifier(HttpContext context)
    {
        // Authenticated: use user ID from JWT claims (stable across instances)
        var userId = context.User.FindFirst("sub")?.Value
                  ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        if (!string.IsNullOrEmpty(userId)) return $"user:{userId}";

        // API key auth: use hashed API key
        var apiKey = context.Request.Headers["Authorization"].ToString();
        if (!string.IsNullOrEmpty(apiKey))
            return $"apikey:{ComputeHash(apiKey)}";

        // Unauthenticated: use IP address
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return $"ip:{ip}";
    }

    // ── Header helpers ────────────────────────────────────────────────────────

    private static void SetRateLimitHeaders(
        HttpContext context, int limit, long remaining, long resetSeconds)
    {
        context.Response.Headers["X-RateLimit-Limit"]     = limit.ToString();
        context.Response.Headers["X-RateLimit-Remaining"] = Math.Max(0, limit - remaining).ToString();
        context.Response.Headers["X-RateLimit-Reset"]     =
            DateTimeOffset.UtcNow.AddSeconds(resetSeconds).ToUnixTimeSeconds().ToString();
    }

    private static string ComputeHash(string input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..16]; // first 16 chars — enough to identify
    }
}

// ════════════════════════════════════════════════════════════════════════════
// TRUE SLIDING WINDOW (sorted set) — for high-security critical endpoints
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// True sliding window using Redis sorted sets (ZADD / ZREMRANGEBYSCORE / ZCARD).
///
/// More accurate than INCR+EXPIRE because the window slides with every request,
/// but costs O(log N) per request and requires a periodic cleanup step.
///
/// Use for: POST /wallets/transfer, POST /payments, POST /payments/{id}/capture
/// Do NOT use for high-frequency read endpoints — INCR+EXPIRE is sufficient there.
///
/// Usage:
///   var checker = new TrueSlidingWindowChecker(redis);
///   var (allowed, count) = await checker.CheckAsync("transfer", userId, limit:10, window:60);
/// </summary>
public class TrueSlidingWindowChecker
{
    private readonly IDatabase _redis;

    public TrueSlidingWindowChecker(IConnectionMultiplexer redis)
    {
        _redis = redis.GetDatabase();
    }

    /// <summary>
    /// Checks and records one request in the sliding window.
    /// Returns (allowed, current count in window).
    /// </summary>
    public async Task<(bool Allowed, long Count)> CheckAsync(
        string policyName,
        string identifier,
        int limit,
        int windowSeconds)
    {
        var key       = $"rl:sw:{policyName}:{identifier}";
        var nowMs     = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var windowMs  = windowSeconds * 1000L;
        var windowStart = nowMs - windowMs;

        try
        {
            // Pipeline: remove expired entries + add current + count remaining
            var tx = _redis.CreateTransaction();

            // Remove entries older than the window start
            _ = tx.SortedSetRemoveRangeByScoreAsync(key, 0, windowStart);

            // Add this request with current timestamp as score
            // Use nowMs + random suffix as member to handle simultaneous requests
            var member = $"{nowMs}:{Guid.NewGuid():N}";
            _ = tx.SortedSetAddAsync(key, member, nowMs);

            // Set key expiry to window duration (cleanup)
            _ = tx.KeyExpireAsync(key, TimeSpan.FromSeconds(windowSeconds + 1));

            // Get count after this request
            var countTask = tx.SortedSetLengthAsync(key, windowStart, nowMs + 1);

            await tx.ExecuteAsync();
            var count = await countTask;

            return (count <= limit, count);
        }
        catch
        {
            return (true, 0); // fail open on Redis error
        }
    }
}

// ════════════════════════════════════════════════════════════════════════════
// RATE LIMIT POLICY FACTORY — centralised policy definitions per system
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Centralised factory for rate limit rule sets per system.
/// Rules are evaluated in order — most specific first.
/// </summary>
public static class RateLimitPolicies
{
        // ── URL Shortener ─────────────────────────────────────────────────────────
    public static IEnumerable<RateLimitRule> UrlPolicies() =>
    [
        // POST /urls — creation limit per user
        new RateLimitRule
        {
            PolicyName = "url-create",
            Limit      = 100,
            Window     = TimeSpan.FromHours(1),
            Paths      = ["/api/v1/urls"],
            Methods    = ["POST"]
        },
        new RateLimitRule
        {
            PolicyName = "authenticated",
            Limit      = 120,
            Window     = TimeSpan.FromMinutes(1)
        }
    ];

}
