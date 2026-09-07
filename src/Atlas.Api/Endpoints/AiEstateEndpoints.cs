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

        // Developer usage reports from the opt-in CLI (analyst role is enough: developers report their own usage).
        group.MapPost("/usage/report", async (UsageReportRequest request, UsageReportService usage, CancellationToken ct) =>
        {
            try
            {
                var entries = (request.Entries ?? []).Select(e => new UsageEntry(e.Period, e.Model, e.InputTokens, e.OutputTokens, e.CacheReadTokens, e.CacheWriteTokens, e.Requests, e.Sessions)).ToList();
                var result = await usage.IngestAsync(request.Actor, request.Tool, "cli", entries, ct);
                return Results.Ok(new UsageReportResponse(result.Accepted, result.Rejected, result.EstimatedCost, "USD", result.UnpricedEntries, result.PriceCatalogVersion));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapGet("/usage", async (UsageReportService usage, CancellationToken ct, int days = 30) =>
        {
            var s = await usage.SummaryAsync(days, ct);
            return s is null ? Results.NoContent() : Results.Ok(new AiUsageSummaryResponse(
                s.Days, s.From, s.To, s.PriceCatalogVersion, s.Currency, s.ReportingActors, s.TotalTokens, s.EstimatedCost, s.UnpricedTokens,
                s.Actors.Select(a => new UsageActorResponse(a.Actor, a.Tools, a.Sessions, a.Requests, a.Tokens, a.EstimatedCost, a.UnpricedTokens, a.LastReportUtc, a.LastPeriod)).ToList(),
                s.ByModel.Select(m => new UsageModelResponse(m.Model, m.Tokens, m.EstimatedCost, m.Actors)).ToList(),
                s.ByDay.Select(d => new UsageDayResponse(d.Period, d.Tokens, d.EstimatedCost, d.Actors)).ToList()));
        });

        // Model prices: the effective list (tenant lines first, then the builtin/config catalog) plus the models seen
        // in reports that nothing prices yet. Writes are admin (the /api/ai prefix rule) and reprice stored estimates.
        group.MapGet("/usage/prices", async (ModelPriceService prices, CancellationToken ct) =>
        {
            var list = await prices.ListAsync(ct);
            return Results.Ok(new PriceCatalogResponse(list.Version, list.BaseVersion, list.Currency, list.Note,
                list.Prices.Select(p => new ModelPriceResponse(p.Id, p.Pattern, p.Input, p.Output, p.CacheRead, p.CacheWrite, p.Source, p.Note, p.UpdatedBy, p.UpdatedAtUtc)).ToList(),
                list.Unpriced.Select(u => new UnpricedModelResponse(u.Model, u.Tokens, u.Actors, u.LastSeen)).ToList()));
        });

        group.MapPut("/usage/prices", async (UpsertModelPriceRequest request, ModelPriceService prices, CancellationToken ct) =>
        {
            try
            {
                var row = await prices.UpsertAsync(request.Pattern, request.Input, request.Output, request.CacheRead, request.CacheWrite, request.Note, request.Author, ct);
                return Results.Ok(new ModelPriceResponse(row.Id, row.Pattern, row.Input, row.Output, row.CacheRead, row.CacheWrite, "tenant", row.Note, row.UpdatedBy, row.UpdatedAtUtc));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapDelete("/usage/prices/{id:guid}", async (Guid id, ModelPriceService prices, CancellationToken ct) =>
            await prices.RemoveAsync(id, ct) ? Results.NoContent() : Results.NotFound());

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
