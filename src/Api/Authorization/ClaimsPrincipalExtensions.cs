using System.Security.Claims;

namespace UrlShortener.Api.Authorization;

/// <summary>
/// Extension methods for ClaimsPrincipal to extract user identity.
/// </summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Extracts the user ID from JWT claims (NameIdentifier or 'sub' claim).
    /// </summary>
    public static string GetUserId(this ClaimsPrincipal principal)
    {
        if (principal == null)
            throw new ArgumentNullException(nameof(principal));

        // Try NameIdentifier first (standard claim)
        var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        
        // Fall back to 'sub' claim (OpenID Connect)
        if (string.IsNullOrEmpty(userId))
            userId = principal.FindFirst("sub")?.Value;

        // If still null, throw
        if (string.IsNullOrEmpty(userId))
            throw new InvalidOperationException("User claim 'NameIdentifier' or 'sub' not found in JWT token.");

        return userId;
    }
}
