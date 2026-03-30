using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace UrlShortener.Api.Authentication;

/// <summary>
/// Development-only authentication scheme that accepts any authorization header
/// and extracts a user ID token. Used for local testing without a real IDP.
/// </summary>
public class DevelopmentAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public DevelopmentAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        try
        {
            // In development: the token is just the user ID (e.g., "Bearer user123")
            var token = authHeader["Bearer ".Length..].Trim();
            if (string.IsNullOrEmpty(token)) return Task.FromResult(AuthenticateResult.Fail("Empty token"));

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, token),
                new Claim("sub", token),
                new Claim(ClaimTypes.Role, "User")
            };

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            Logger.LogDebug("Development auth: user={UserId}", token);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Development authentication failed");
            return Task.FromResult(AuthenticateResult.Fail("Authentication failed"));
        }
    }
}
