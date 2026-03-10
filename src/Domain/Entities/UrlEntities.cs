namespace UrlShortener.Domain.Entities;

public class ShortUrl
{
    public string Id { get; private set; } = string.Empty;
    public string ShortCode { get; private set; } = string.Empty;
    public string OriginalUrl { get; private set; } = string.Empty;
    public string CreatedBy { get; private set; } = string.Empty;
    public string? Title { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public bool IsActive { get; private set; }
    public long ClickCount { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value <= DateTimeOffset.UtcNow;
    public bool IsServable => IsActive && !IsExpired;

    public void Deactivate() => throw new NotImplementedException();
    public void Update(string? title, DateTimeOffset? expiresAt, bool? isActive) => throw new NotImplementedException();
}

public class UrlClick
{
    public string Id { get; private set; } = string.Empty;
    public string UrlId { get; private set; } = string.Empty;
    public DateTimeOffset ClickedAt { get; private set; }
    public string? UserAgent { get; private set; }
    public string? Referrer { get; private set; }
    public string? CountryCode { get; private set; }
    public string? IpHash { get; private set; } // hashed for privacy
}

namespace UrlShortener.Api;
using System.Security.Claims;
public static class ClaimsPrincipalExtensions
{
    public static string GetUserId(this ClaimsPrincipal p)
    {
        var id = p.FindFirstValue(ClaimTypes.NameIdentifier) ?? p.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(id)) throw new UnauthorizedAccessException("User ID claim missing.");
        return id;
    }
}