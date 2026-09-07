using Atlas.Api;
using Atlas.Api.Endpoints;
using Atlas.Application;
using Atlas.Application.Assessments;
using Atlas.Application.Credentials;
using Atlas.Application.Findings;
using Atlas.Connector.Abstractions;
using Atlas.Connector.AzureDevOps;
using Atlas.Connector.Git;
using Atlas.Connector.GitHub;
using Atlas.Connector.GitLab;
using Atlas.Ai;
using Atlas.Application.Ai;
using Atlas.Application.Security;
using Atlas.Application.Tenants;
using Atlas.Connector.Upload;
using Atlas.Language.Abstractions;
using Atlas.Language.CSharp;
using Atlas.Language.Sql;
using Atlas.Language.VisualBasic;
using Atlas.Connector.Local;
using Atlas.Contracts.Assessments;
using Atlas.Domain.Assessments;
using Atlas.Domain.Findings;
using Atlas.Domain.Sources;
using Atlas.Domain.Tenants;
using Atlas.Governance.Infrastructure;
using Atlas.Infrastructure;
using Atlas.Infrastructure.Persistence;
using Atlas.Reporting;
using Atlas.Scanner.Ai;
using Atlas.Scanner.Architecture;
using Atlas.Scanner.Database;
using Atlas.Scanner.JavaScript;
using Atlas.Scanner.Licenses;
using Atlas.Scanner.Dependencies;
using Atlas.Scanner.Infrastructure;
using Atlas.Scanner.Privacy;
using Atlas.Scanner.Quality;
using Atlas.Scanner.Runtime;
using Atlas.Scanner.Secrets;
using Atlas.Scanner.Security;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Api.Endpoints;

/// <summary>Who am I (/api/auth/me) and the tenant registry.</summary>
internal static class TenantEndpoints
{
    public static void Map(WebApplication app)
    {
        // ---- Tenants: admin-only registry mapping identity-token claims to isolation boundaries ----
        app.MapGet("/api/auth/me", (HttpContext http, ITenantContext tenant, AuthOptions auth) => Results.Ok(new
        {
            name = http.User.Identity?.IsAuthenticated == true ? (http.User.FindFirst("name")?.Value ?? http.User.FindFirst("preferred_username")?.Value ?? http.User.Identity.Name) : null,
            tenantId = tenant.TenantId,
            tenantName = tenant.TenantName,
            isDefaultTenant = tenant.TenantId == WellKnownTenants.DefaultId,
            roles = http.User.Claims.Where(c => c.Type == auth.RoleClaim || c.Type == System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).Distinct().ToArray(),
        }));

        var tenantsGroup = app.MapGroup("/api/tenants").RequireRateLimiting("api");

        tenantsGroup.MapGet("/", async (ITenantRepository tenants, CancellationToken ct) =>
            Results.Ok((await tenants.ListAsync(ct)).Select(t => new TenantResponse(t.Id, t.Name, t.ExternalKey, t.CreatedAtUtc, t.Id == WellKnownTenants.DefaultId))));

        tenantsGroup.MapPost("/", async (TenantRequest request, ITenantRepository tenants, IUnitOfWork unitOfWork, CancellationToken ct) =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(request.ExternalKey) && await tenants.GetByExternalKeyAsync(request.ExternalKey.Trim(), ct) is not null)
                {
                    return Results.Conflict(new { error = $"External key '{request.ExternalKey}' is already mapped." });
                }

                var tenant = new Tenant(Guid.NewGuid(), request.Name);
                tenant.Update(request.Name, request.ExternalKey);
                tenants.Add(tenant);
                await unitOfWork.SaveChangesAsync(ct);
                return Results.Created($"/api/tenants/{tenant.Id}", new TenantResponse(tenant.Id, tenant.Name, tenant.ExternalKey, tenant.CreatedAtUtc, false));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        tenantsGroup.MapPatch("/{tenantId:guid}", async (Guid tenantId, TenantRequest request, ITenantRepository tenants, IUnitOfWork unitOfWork, CancellationToken ct) =>
        {
            var tenant = await tenants.GetAsync(tenantId, ct);
            if (tenant is null)
            {
                return Results.NotFound();
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(request.ExternalKey))
                {
                    var clash = await tenants.GetByExternalKeyAsync(request.ExternalKey.Trim(), ct);
                    if (clash is not null && clash.Id != tenantId)
                    {
                        return Results.Conflict(new { error = $"External key '{request.ExternalKey}' is already mapped." });
                    }
                }

                tenant.Update(request.Name, request.ExternalKey);
                await unitOfWork.SaveChangesAsync(ct);
                return Results.Ok(new TenantResponse(tenant.Id, tenant.Name, tenant.ExternalKey, tenant.CreatedAtUtc, tenant.Id == WellKnownTenants.DefaultId));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
