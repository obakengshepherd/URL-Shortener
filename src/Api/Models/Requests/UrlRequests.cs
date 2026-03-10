namespace UrlShortener.Api.Models.Requests;

using System.ComponentModel.DataAnnotations;

public record CreateUrlRequest
{
    [Required][Url][StringLength(2048)] public string OriginalUrl { get; init; } = string.Empty;
    [RegularExpression(@"^[a-zA-Z0-9\-]{4,32}$", ErrorMessage = "Alias must be 4–32 alphanumeric chars or hyphens.")]
    public string? Alias { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    [StringLength(128)] public string? Title { get; init; }
}

public record UpdateUrlRequest
{
    [StringLength(128)] public string? Title { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool? IsActive { get; init; }
}

public record GetStatsRequest
{
    public string? From { get; init; }
    public string? To { get; init; }
    public string Granularity { get; init; } = "day"; // hour | day | week
}