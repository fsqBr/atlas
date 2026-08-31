using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Atlas.Api;

public sealed class AuthOptions
{
    public const string SectionName = "Atlas:Auth";

    /// <summary>Off by default: a fresh install works without an identity provider. Turn on for anything shared.</summary>
    public bool Enabled { get; set; }

    /// <summary>OIDC issuer, e.g. https://login.microsoftonline.com/{tenant}/v2.0 or https://keycloak.example.com/realms/atlas.</summary>
    public string? Authority { get; set; }

    /// <summary>Expected `aud` of access tokens (API application id). Null skips audience validation.</summary>
    public string? Audience { get; set; }

    /// <summary>Public SPA client id used by the web UI (authorization code + PKCE).</summary>
    public string? ClientId { get; set; }

    public string Scopes { get; set; } = "openid profile email";

    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Claim carrying roles (Keycloak: "roles" or "realm_access.roles" flattened; Entra: "roles").</summary>
    public string RoleClaim { get; set; } = "roles";

    /// <summary>Everything, including credentials, tenant-wide policies and deletion.</summary>
    public string AdminRole { get; set; } = "atlas-admin";

    /// <summary>Create/run assessments, triage, policies, scope, schedule.</summary>
    public string AnalystRole { get; set; } = "atlas-analyst";
}

/// <summary>
/// OIDC bearer authentication for the API. When enabled, every /api/* route
/// except /api/auth/config requires a valid access token from the configured
/// authority; health endpoints stay open for probes. The SPA reads
/// /api/auth/config to run the authorization-code + PKCE flow itself.
/// </summary>
public static class AuthSetup
{
    public const string SmartScheme = "AtlasBearer";

    public static AuthOptions AddAtlasAuth(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
        builder.Services.AddSingleton(options);

        if (!options.Enabled)
        {
            return options;
        }

        if (string.IsNullOrWhiteSpace(options.Authority) || string.IsNullOrWhiteSpace(options.ClientId))
        {
            throw new InvalidOperationException("Atlas:Auth:Enabled is true but Atlas:Auth:Authority and Atlas:Auth:ClientId are required.");
        }

        // Two bearer flavours behind one default scheme: atlas_pat_… service tokens (CI) and OIDC access tokens (people).
        builder.Services
            .AddAuthentication(SmartScheme)
            .AddPolicyScheme(SmartScheme, "Atlas bearer", policy =>
            {
                policy.ForwardDefaultSelector = context =>
                {
                    var header = context.Request.Headers.Authorization.ToString();
                    return header.StartsWith("Bearer " + Atlas.Domain.Security.ApiToken.Prefix, StringComparison.Ordinal)
                        ? ApiTokenAuthenticationHandler.SchemeName
                        : JwtBearerDefaults.AuthenticationScheme;
                };
            })
            .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(ApiTokenAuthenticationHandler.SchemeName, null)
            .AddJwtBearer(jwt =>
            {
                jwt.Authority = options.Authority;
                jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
                jwt.MapInboundClaims = false;

                // Browser navigations (report iframe, PDF download) cannot set a header: accept the token as a
                // query parameter on those two routes only.
                jwt.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var path = context.Request.Path.Value ?? string.Empty;
                        if (string.IsNullOrEmpty(context.Token)
                            && (path.EndsWith("/report", StringComparison.Ordinal) || path.EndsWith("/report.pdf", StringComparison.Ordinal) || path.EndsWith("/findings/export", StringComparison.Ordinal))
                            && context.Request.Query.TryGetValue("access_token", out var token))
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    },
                };

                if (string.IsNullOrWhiteSpace(options.Audience))
                {
                    jwt.TokenValidationParameters.ValidateAudience = false;
                }
                else
                {
                    jwt.Audience = options.Audience;
                }
            });

        return options;
    }

    public static void UseAtlasAuth(this WebApplication app, AuthOptions options)
    {
        if (!options.Enabled)
        {
            return;
        }

        app.UseAuthentication();
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            var protectedRoute = path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/auth") && !path.StartsWithSegments("/api/version");
            if (protectedRoute && context.User.Identity?.IsAuthenticated != true)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await context.Response.WriteAsJsonAsync(new { error = "Authentication required." });
                return;
            }

            if (protectedRoute && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            {
                var required = RequiredRole(options, context.Request.Method, path.Value ?? string.Empty);
                if (!HasRole(context.User, options, required))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { error = $"Role '{required}' required." });
                    return;
                }
            }

            await next(context);
        });
    }

    /// <summary>Viewer = any authenticated user (read-only); analyst = day-to-day work; admin = credentials, tenant policies, deletion.</summary>
    internal static string RequiredRole(AuthOptions options, string method, string path)
    {
        var admin = path.StartsWith("/api/credentials", StringComparison.OrdinalIgnoreCase)
            || (path.StartsWith("/api/policies", StringComparison.OrdinalIgnoreCase))
            || (path.StartsWith("/api/ai", StringComparison.OrdinalIgnoreCase) && !HttpMethods.IsGet(method))
            || (path.StartsWith("/api/rules", StringComparison.OrdinalIgnoreCase) && !HttpMethods.IsGet(method))
            || (path.StartsWith("/api/settings/cost", StringComparison.OrdinalIgnoreCase) && !HttpMethods.IsGet(method))
            || path.StartsWith("/api/tenants", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/tokens", StringComparison.OrdinalIgnoreCase)
            || (HttpMethods.IsDelete(method) && path.StartsWith("/api/assessments", StringComparison.OrdinalIgnoreCase) && path.Count(ch => ch == '/') == 3);
        return admin ? options.AdminRole : options.AnalystRole;
    }

    internal static bool HasRole(System.Security.Claims.ClaimsPrincipal user, AuthOptions options, string required)
    {
        var roles = user.Claims.Where(c => c.Type == options.RoleClaim || c.Type == System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return roles.Contains(options.AdminRole) || (required == options.AnalystRole && roles.Contains(options.AnalystRole));
    }

    /// <summary>What the SPA needs to sign in; never includes secrets (public client).</summary>
    public static object ToPublicConfig(this AuthOptions options) => new
    {
        enabled = options.Enabled,
        authority = options.Enabled ? options.Authority : null,
        clientId = options.Enabled ? options.ClientId : null,
        scopes = options.Enabled ? options.Scopes : null,
    };
}
