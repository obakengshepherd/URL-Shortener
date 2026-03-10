using UrlShortener.Application.Interfaces;
using UrlShortener.Api.Models.Requests;
using UrlShortener.Api.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace UrlShortener.Api.Controllers;

[ApiController]
[Route("api/v1/urls")]
[Authorize]
public class UrlController : ControllerBase
{
    private readonly IUrlService _urlService;
    private readonly IAnalyticsService _analyticsService;

    public UrlController(IUrlService urlService, IAnalyticsService analyticsService)
    {
        _urlService = urlService;
        _analyticsService = analyticsService;
    }

    // POST /api/v1/urls
    [HttpPost]
    public async Task<IActionResult> CreateUrl(
        [FromBody] CreateUrlRequest request,
        [FromHeader(Name = "X-Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        var result = await _urlService.CreateUrlAsync(userId, idempotencyKey, request, ct);
        return StatusCode(StatusCodes.Status201Created, ApiResponse.Success(result));
    }

    // GET /api/v1/urls/{code}/stats
    [HttpGet("{code}/stats")]
    public async Task<IActionResult> GetStats(
        [FromRoute] string code,
        [FromQuery] GetStatsRequest query,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        var result = await _analyticsService.GetStatsAsync(code, userId, query, ct);
        return Ok(ApiResponse.Success(result));
    }

    // DELETE /api/v1/urls/{code}
    [HttpDelete("{code}")]
    public async Task<IActionResult> DeactivateUrl([FromRoute] string code, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var result = await _urlService.DeactivateAsync(code, userId, ct);
        return Ok(ApiResponse.Success(result));
    }

    // PATCH /api/v1/urls/{code}
    [HttpPatch("{code}")]
    public async Task<IActionResult> UpdateUrl(
        [FromRoute] string code,
        [FromBody] UpdateUrlRequest request,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        var result = await _urlService.UpdateAsync(code, userId, request, ct);
        return Ok(ApiResponse.Success(result));
    }
}

/// <summary>
/// Handles the high-throughput redirect path. Public — no auth required.
/// Resolves short codes via Redis cache, falls back to PostgreSQL.
/// </summary>
[ApiController]
public class RedirectController : ControllerBase
{
    private readonly IRedirectService _redirectService;

    public RedirectController(IRedirectService redirectService)
    {
        _redirectService = redirectService;
    }

    // GET /{code}  — lives on redirect subdomain
    [HttpGet("/{code}")]
    [AllowAnonymous]
    public async Task<IActionResult> Redirect([FromRoute] string code, CancellationToken ct)
    {
        var result = await _redirectService.ResolveAsync(code, HttpContext, ct);

        return result.Status switch
        {
            RedirectStatus.Found => RedirectPermanent(result.OriginalUrl!),
            RedirectStatus.NotFound => NotFound(),
            RedirectStatus.Gone => StatusCode(StatusCodes.Status410Gone),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }
}