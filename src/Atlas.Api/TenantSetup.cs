using Atlas.Application.Tenants;
using Atlas.Domain.Tenants;
using Atlas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Atlas.Api;

public sealed class TenantOptions
{
    public const string SectionName = "Atlas:Tenants";

    /// <summary>Token claim that names the tenant (Entra: "tid"; Keycloak: a mapper such as "atlas_tenant").</summary>
    public string Claim { get; set; } = "tid";

    /// <summary>When the token has no mapped tenant: true → the default tenant (single-tenant installs), false → 403.</summary>
    public bool AllowUnmappedUsers { get; set; } = true;

    /// <summary>Header accepted only while auth is off (dev/tests): tenant external key or id.</summary>
    public string Header { get; set; } = "X-Atlas-Tenant";
}

/// <summary>Per-request tenant. Unresolved until the middleware ran; reading it before that throws (fail closed).</summary>
public sealed class HttpTenantContext(IHttpContextAccessor accessor) : ITenantContext
{
    private Guid? _tenantId;
    private bool _resolved;

    public Guid? TenantId
    {
        get
        {
            if (!_resolved)
            {
                Inherit();
            }

            return _tenantId;
        }
    }

    /// <summary>
    /// A scope created outside the middleware (connector credential lookups, background
    /// services) inherits the ambient request's tenant when there is one, and runs in
    /// system scope when there is no request at all. A request whose middleware did not
    /// run stays unresolved and fails closed.
    /// </summary>
    private void Inherit()
    {
        var http = accessor.HttpContext;
        if (http is null)
        {
            UseSystemScope();
            return;
        }

        var ambient = http.RequestServices.GetService<HttpTenantContext>();
        if (ambient is not null && !ReferenceEquals(ambient, this) && ambient._resolved)
        {
            _tenantId = ambient._tenantId;
            TenantName = ambient.TenantName;
            Subject = ambient.Subject;
            SubjectName = ambient.SubjectName;
            IsAdmin = ambient.IsAdmin;
            _resolved = true;
            return;
        }

        throw new InvalidOperationException("Tenant has not been resolved for this request (tenant middleware did not run).");
    }

    public string? TenantName { get; private set; }

    public string? Subject { get; private set; }

    public string? SubjectName { get; private set; }

    public bool IsAdmin { get; private set; } = true;

    public void Set(Guid tenantId, string name)
    {
        _tenantId = tenantId;
        TenantName = name;
        _resolved = true;
    }

    /// <summary>Identity for per-assessment access: anonymous installations act as admin (nothing to enforce against).</summary>
    public void SetSubject(string? subject, string? subjectName, bool isAdmin)
    {
        Subject = subject;
        SubjectName = subjectName;
        IsAdmin = isAdmin;
    }

    /// <summary>Background services (schedules, GC, sync) act across tenants.</summary>
    public void UseSystemScope()
    {
        _tenantId = null;
        TenantName = null;
        Subject = null;
        SubjectName = null;
        IsAdmin = true;
        _resolved = true;
    }
}

public static class TenantSetup
{
    public static TenantOptions AddAtlasTenants(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(TenantOptions.SectionName).Get<TenantOptions>() ?? new TenantOptions();
        builder.Services.AddSingleton(options);
        builder.Services.AddMemoryCache();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<HttpTenantContext>();
        builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<HttpTenantContext>());
        return options;
    }

    /// <summary>Resolves the tenant for every request, after authentication and before endpoints.</summary>
    public static void UseAtlasTenants(this WebApplication app, TenantOptions options, AuthOptions auth)
    {
        app.Use(async (context, next) =>
        {
            var tenant = context.RequestServices.GetRequiredService<HttpTenantContext>();
            var cache = context.RequestServices.GetRequiredService<IMemoryCache>();
            var db = context.RequestServices.GetRequiredService<AtlasDbContext>();
            ResolveSubject(context, tenant, auth);

            string? key = null;
            var fromToken = false;
            if (context.User.FindFirst(ApiTokenAuthenticationHandler.TenantClaim)?.Value is { } tokenTenant && Guid.TryParse(tokenTenant, out var tokenTenantId))
            {
                // Service token: tenant fixed at creation, no external-key mapping needed.
                var name = await cache.GetOrCreateAsync($"tenant-id:{tokenTenantId}", async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
                    return (await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tokenTenantId))?.Name;
                });
                if (name is null)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { error = "The token's tenant no longer exists." });
                    return;
                }

                tenant.Set(tokenTenantId, name);
                await next(context);
                return;
            }

            if (auth.Enabled && context.User.Identity?.IsAuthenticated == true)
            {
                key = context.User.FindFirst(options.Claim)?.Value;
                fromToken = true;
            }
            else if (!auth.Enabled && context.Request.Headers.TryGetValue(options.Header, out var header))
            {
                key = header.ToString();
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                if (fromToken && !options.AllowUnmappedUsers)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { error = $"Token has no '{options.Claim}' claim; this installation does not allow unmapped users." });
                    return;
                }

                tenant.Set(WellKnownTenants.DefaultId, "Default");
                await next(context);
                return;
            }

            var resolved = await cache.GetOrCreateAsync<(Guid Id, string Name)?>($"tenant:{key}", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
                var byKey = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.ExternalKey == key);
                if (byKey is null && Guid.TryParse(key, out var id))
                {
                    byKey = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
                }

                return byKey is null ? null : (byKey.Id, byKey.Name);
            });

            if (resolved is null)
            {
                if (fromToken && options.AllowUnmappedUsers)
                {
                    tenant.Set(WellKnownTenants.DefaultId, "Default");
                    await next(context);
                    return;
                }

                context.Response.StatusCode = fromToken ? StatusCodes.Status403Forbidden : StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = fromToken ? $"No tenant is mapped to '{key}'. An administrator must register it under /api/tenants." : $"Unknown tenant '{key}'." });
                return;
            }

            tenant.Set(resolved.Value.Id, resolved.Value.Name);
            await next(context);
        });
    }

    /// <summary>Subject = token id for service tokens, otherwise the OIDC subject (fallback e-mail/username); admin = role or no auth.</summary>
    private static void ResolveSubject(HttpContext context, HttpTenantContext tenant, AuthOptions auth)
    {
        var user = context.User;
        if (!auth.Enabled || user.Identity?.IsAuthenticated != true)
        {
            tenant.SetSubject(null, null, isAdmin: true);
            return;
        }

        var tokenId = user.FindFirst(ApiTokenAuthenticationHandler.TokenIdClaim)?.Value;
        var subject = tokenId is not null
            ? $"token:{tokenId}"
            : user.FindFirst("sub")?.Value ?? user.FindFirst("email")?.Value ?? user.FindFirst("preferred_username")?.Value ?? user.Identity.Name;
        var name = user.FindFirst("name")?.Value ?? user.FindFirst("preferred_username")?.Value ?? user.FindFirst("email")?.Value ?? subject;
        tenant.SetSubject(subject, name, AuthSetup.HasRole(user, auth, auth.AdminRole));
    }
}
