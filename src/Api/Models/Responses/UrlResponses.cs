namespace UrlShortener.Api.Models.Responses;

public record UrlResponse
{
    public string Id { get; init; } = string.Empty;
    public string ShortCode { get; init; } = string.Empty;
    public string ShortUrl { get; init; } = string.Empty;
    public string OriginalUrl { get; init; } = string.Empty;
    public string? Title { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool IsActive { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public record UrlDeactivatedResponse
{
    public string ShortCode { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public DateTimeOffset DeactivatedAt { get; init; }
}

public record UrlStatsResponse
{
    public string ShortCode { get; init; } = string.Empty;
    public long TotalClicks { get; init; }
    public long UniqueClicks { get; init; }
    public IEnumerable<ClicksByPeriod> ClicksByPeriod { get; init; } = [];
    public IEnumerable<ReferrerStat> TopReferrers { get; init; } = [];
}

public record ClicksByPeriod { public string Period { get; init; } = string.Empty; public long Clicks { get; init; } }
public record ReferrerStat { public string Referrer { get; init; } = string.Empty; public long Clicks { get; init; } }

public record RedirectResult
{
    public RedirectStatus Status { get; init; }
    public string? OriginalUrl { get; init; }
}

public enum RedirectStatus { Found, NotFound, Gone }

public record ApiResponse<T> { public T Data { get; init; } = default!; public ApiMeta Meta { get; init; } = new(); }
public record ApiMeta { public string RequestId { get; init; } = Guid.NewGuid().ToString(); public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow; }
public static class ApiResponse { public static ApiResponse<T> Success<T>(T data) => new() { Data = data }; }