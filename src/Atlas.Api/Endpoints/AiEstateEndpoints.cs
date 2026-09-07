using Atlas.Application.AiEstate;
using Atlas.Application.Assessments;
using Atlas.Application.Tenants;
using Atlas.Domain.AiEstate;
using Atlas.Scanner.Ai;
using Atlas.Scanner.Ai.Catalog;
using Atlas.Application.Portfolio;
using Atlas.Contracts.Assessments;
using Atlas.Governance.Application;
using Atlas.Governance.Contracts;
using Atlas.Governance.Domain;

namespace Atlas.Api.Endpoints;

/// <summary>
/// The governance module's endpoints, mapped by the API host: the estate-wide AI inventory,
/// the cost sources (admin) and the read-only cost sync/summary. Keys never travel through these routes —
/// a source references a credential stored under /api/credentials by name.
/// </summary>
internal static class AiEstateEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/ai-estate").RequireRateLimiting("api");

        // The AI estate across the tenant's assessments (the portfolio section as JSON), incl. spend when connected.
        group.MapGet("/", async (PortfolioBuilder portfolio, CancellationToken ct, string? lang = null, string? tag = null) =>
        {
            var summary = await portfolio.BuildAsync(lang, ct, tag);
            return summary.AiEstate is null ? Results.NoContent() : Results.Ok(ApiMapping.ToResponse(summary.AiEstate));
        });

        group.MapGet("/cost/sources", async (CostSourceService service, CancellationToken ct) =>
            Results.Ok((await service.ListAsync(ct)).Select(ToResponse)));

        group.MapPut("/cost/sources/{provider}", async (string provider, UpsertCostSourceRequest request, CostSourceService service, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(ToResponse(await service.UpsertAsync(provider, request.CredentialName, request.Scope, request.Enabled, ct)));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapDelete("/cost/sources/{provider}", async (string provider, CostSourceService service, CancellationToken ct) =>
            await service.DeleteAsync(provider, ct) ? Results.NoContent() : Results.NotFound());

        // Manual, read-only pull from every enabled source. Per-source outcomes; a failing provider never hides the others.
        group.MapPost("/cost/sync", async (CostSyncRequest? request, CostSyncService sync, CancellationToken ct) =>
        {
            var results = await sync.SyncAsync(request?.Days ?? CostSyncService.DefaultDays, ct);
            return Results.Ok(results.Select(r => new CostSyncResultResponse(r.Provider, r.Succeeded, r.Facts, r.Seats, r.Error)));
        });

        group.MapGet("/cost", async (IAiCostSummarySource costs, CancellationToken ct, int days = 30) =>
        {
            var summary = await costs.GetAsync(days, ct);
            return summary is null ? Results.NoContent() : Results.Ok(ApiMapping.ToResponse(summary));
        });

        group.MapGet("/cost/providers", () => Results.Ok(CostProviders.All));

        // The signature catalog's providers: what the allowlist can name.
        group.MapGet("/catalog/providers", (SignatureCatalog catalog) =>
            Results.Ok(catalog.Document.Providers.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p => new AiCatalogProviderResponse(p.Id, p.Name, p.Kind))));

        // Approved providers (the V0.5 policy). Saved per tenant in the UI; the deployment config is the fallback.
        group.MapGet("/allowlist", async (ITenantAiEstateSettingsRepository settings, AiEstateOptions options, ITenantContext tenant, CancellationToken ct) =>
        {
            var saved = await settings.GetForTenantAsync(tenant.Require(), ct);
            if (saved is { HasAllowlist: true })
            {
                return Results.Ok(new AiEstateAllowlistResponse(saved.ApprovedProviders, "tenant", saved.UpdatedBy, saved.UpdatedAtUtc));
            }

            var fromConfig = options.ApprovedProviders.Select(p => p.Trim().ToLowerInvariant()).Where(p => p.Length > 0).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
            return Results.Ok(new AiEstateAllowlistResponse(fromConfig, fromConfig.Count > 0 ? "config" : "none", null, null));
        });

        group.MapPut("/allowlist", async (AiEstateAllowlistRequest request, ITenantAiEstateSettingsRepository settings, SignatureCatalog catalog, ITenantContext tenant, IUnitOfWork unitOfWork, CancellationToken ct) =>
        {
            var unknown = (request.ApprovedProviders ?? []).Select(p => p.Trim().ToLowerInvariant()).Where(p => p.Length > 0 && catalog.Provider(p) is null).Distinct().ToList();
            if (unknown.Count > 0)
            {
                return Results.BadRequest(new { error = $"Unknown provider id(s): {string.Join(", ", unknown)}. Use ids from /api/ai-estate/catalog/providers." });
            }

            var author = string.IsNullOrWhiteSpace(request.Author) ? tenant.SubjectName ?? tenant.Subject ?? "unknown" : request.Author.Trim();
            var saved = await settings.GetForTenantAsync(tenant.Require(), ct);
            try
            {
                if (saved is null)
                {
                    saved = new TenantAiEstateSettings(tenant.Require(), request.ApprovedProviders ?? [], author);
                    settings.Add(saved);
                }
                else
                {
                    saved.Update(request.ApprovedProviders ?? [], author);
                }
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            await unitOfWork.SaveChangesAsync(ct);
            return Results.Ok(new AiEstateAllowlistResponse(saved.ApprovedProviders, saved.HasAllowlist ? "tenant" : "none", saved.UpdatedBy, saved.UpdatedAtUtc));
        });

        group.MapDelete("/allowlist", async (ITenantAiEstateSettingsRepository settings, ITenantContext tenant, IUnitOfWork unitOfWork, CancellationToken ct) =>
        {
            var saved = await settings.GetForTenantAsync(tenant.Require(), ct);
            if (saved is null)
            {
                return Results.NoContent();
            }

            settings.Remove(saved);
            await unitOfWork.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // One assessment's AI estate for the detail tab: inventory record, correlation counters and the open AI rules.
        app.MapGet("/api/assessments/{id:guid}/ai-estate", async (Guid id, AssessmentAiEstateBuilder builder, CancellationToken ct, string? lang = null) =>
        {
            var estate = await builder.BuildAsync(id, lang, ct);
            return estate is null ? Results.NotFound() : Results.Ok(ApiMapping.ToResponse(estate));
        }).RequireRateLimiting("api");
    }

    private static CostSourceResponse ToResponse(CostSource s) => new(
        s.Provider, s.CredentialName, s.Scope, s.Enabled, s.CreatedAtUtc, s.UpdatedAtUtc, s.LastSyncAtUtc, s.LastSyncStatus, s.LastSyncError, s.LastSyncFacts);
}
