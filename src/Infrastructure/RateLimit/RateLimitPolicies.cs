using System.Collections.Generic;
using System.Linq;
using Shared.Infrastructure.RateLimit;

namespace UrlShortener.Infrastructure.RateLimit;

/// <summary>
/// Rate limit policies for the URL Shortener API.
/// 
/// Policies:
///   - URL Creation (POST /urls): 100/hour per user (critical write path)
///   - List/Read Operations: 120/minute per user (standard)
///   - Analytics: 50/minute per user (read-heavy)
/// </summary>
public static class RateLimitPolicies
{
    public static IEnumerable<RateLimitRule> UrlPolicies()
    {
        return new[]
        {
            // ── Critical write paths ──────────────────────────────────────────

            new RateLimitRule
            {
                PolicyName = "url-create",
                Limit      = 100,
                Window     = TimeSpan.FromHours(1),
                Paths      = new[] { "/api/v1/urls" },
                Methods    = new[] { "POST" }
            },

            // ── URL management operations ──────────────────────────────────────

            new RateLimitRule
            {
                PolicyName = "url-management",
                Limit      = 120,
                Window     = TimeSpan.FromMinutes(1),
                Paths      = new[] { "/api/v1/urls" },
                Methods    = new[] { "GET", "PATCH", "DELETE" }
            },

            // ── Analytics reads ────────────────────────────────────────────────

            new RateLimitRule
            {
                PolicyName = "analytics",
                Limit      = 50,
                Window     = TimeSpan.FromMinutes(1),
                Paths      = new[] { "/api/v1/urls/stats", "/api/v1/stats" },
                Methods    = new[] { "GET" }
            },

            // ── Fallback: general API limit ────────────────────────────────────

            new RateLimitRule
            {
                PolicyName = "general",
                Limit      = 120,
                Window     = TimeSpan.FromMinutes(1),
                Paths      = null, // applies to all paths not covered above
                Methods    = null
            }
        };
    }
}
