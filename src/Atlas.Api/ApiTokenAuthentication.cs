using System.Security.Claims;
using System.Text.Encodings.Web;
using Atlas.Application.Security;
using Atlas.Domain.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Atlas.Api;

/// <summary>
/// Bearer scheme for <c>atlas_pat_…</c> tokens. Runs next to OIDC: a policy scheme
/// routes each request by the token's shape, so CI can call the same endpoints with
/// a service token while people sign in through the identity provider. The
/// principal carries the tenant id directly (claim <see cref="TenantClaim"/>) and
/// the role in the configured role claim, so RBAC and tenant resolution work unchanged.
/// </summary>
public sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AuthOptions auth) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AtlasApiToken";
    public const string TenantClaim = "atlas_tenant_id";
    public const string TokenIdClaim = "atlas_token_id";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var secret = header["Bearer ".Length..].Trim();
        if (!ApiToken.LooksLikeToken(secret))
        {
            return AuthenticateResult.NoResult();
        }

        // Authentication runs before the tenant middleware: use the request scope in system mode.
        Context.RequestServices.GetRequiredService<HttpTenantContext>().UseSystemScope();
        var tokens = Context.RequestServices.GetRequiredService<ApiTokenService>();
        var identity = await tokens.AuthenticateAsync(secret, Context.RequestAborted);
        if (identity is null)
        {
            return AuthenticateResult.Fail("Unknown, revoked or expired API token.");
        }

        var role = identity.Role == ApiToken.Roles.Admin ? auth.AdminRole : auth.AnalystRole;
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, $"token:{identity.Name}"),
            new("name", $"token:{identity.Name}"),
            new(auth.RoleClaim, role),
            new(TenantClaim, identity.TenantId.ToString()),
            new(TokenIdClaim, identity.TokenId.ToString()),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, auth.RoleClaim));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
