using Atlas.Application.AiEstate;
using Atlas.Application.Assessments;
using Atlas.Application.Tenants;
using Atlas.Domain.AiEstate;
using Atlas.Scanner.Ai;
using Atlas.Scanner.Ai.Catalog;
using Atlas.Application.Portfolio;
using Atlas.Contracts.Assessments;
using Atlas.Governance.Application;
using Atlas.Governance.Application.Telemetry;
using Atlas.Governance.Domain;
using Atlas.Governance.Contracts;

namespace Atlas.Api.Endpoints;

/// <summary>
/// The governance module's endpoints, mapped by the API host: the estate-wide AI inventory,
/// the cost sources (admin) and the read-only cost sync/summary. Keys never travel through these routes —
/// a source references a credential stored under /api/credentials by name.
/// </summary>
internal static class AiEstateEndpoints
{
    private static BudgetResponse BudgetRow(BudgetStatus s) =>
        new(s.Budget.Id, s.Budget.Scope.ToString().ToLowerInvariant(), s.Budget.ScopeKey, s.Budget.MonthlyAmount, s.Budget.Name, s.Budget.Label, s.Budget.Enabled, s.Budget.UpdatedBy, s.Budget.UpdatedAtUtc,
            s.SpentMonthToDate, s.Percent, s.ProjectedMonth, s.ProjectedPercent, s.State, s.DaysElapsed, s.DaysInMonth);

    private static TeamResponse TeamRow(Team t) => new(t.Id, t.Name, t.Members, t.UpdatedBy, t.UpdatedAtUtc, t.WebhookUrl, t.SlackWebhookUrl, t.TeamsWebhookUrl);

    private static async Task<IResult> Otlp(HttpRequest http, TelemetryIngestService telemetry, bool traces, CancellationToken ct)
    {
        var contentType = http.ContentType ?? "";
        if (contentType.Contains("protobuf", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(new { error = "This receiver reads OTLP/HTTP JSON. Set OTEL_EXPORTER_OTLP_PROTOCOL=http/json on the sender (or encoding: json on the collector's otlphttp exporter)." }, statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        System.Text.Json.JsonDocument doc;
        try
        {
            doc = await System.Text.Json.JsonDocument.ParseAsync(http.Body, cancellationToken: ct);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return Results.BadRequest(new { error = "Invalid OTLP JSON: " + ex.Message });
        }

        using (doc)
        {
            var result = traces ? await telemetry.IngestTracesAsync(doc.RootElement, ct) : await telemetry.IngestMetricsAsync(doc.RootElement, ct);
            // OTLP success body is an (optionally partial-success) object; the counts are an Atlas extension for curl users.
            return Results.Ok(new { partialSuccess = new { }, atlas = new TelemetryIngestResponse(result.Accepted, result.Ignored, result.Streams, result.Notes) });
        }
    }

    private static UsageForecastResponse Forecast(UsageForecast f) =>
        new(f.MonthToDateCost, f.DaysElapsedInMonth, f.DaysInMonth, f.ProjectedMonthCost, f.DailyAverage7, f.DailyAverage30, f.ProjectedNext30Cost, f.TrendPercent, f.MonthToDateTokens, f.UnpricedTokensMonthToDate,
            f.ProjectedMonthSeasonal, f.ProjectedMonthLow, f.ProjectedMonthHigh, f.ProjectedMonthCalibrated, f.CalibrationRatio, f.CumulativeByDay ?? []);

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
                s.Actors.Select(a => new UsageActorResponse(a.Actor, a.Tools, a.Sessions, a.Requests, a.Tokens, a.EstimatedCost, a.UnpricedTokens, a.LastReportUtc, a.LastPeriod, Forecast(a.Forecast))).ToList(),
                s.ByModel.Select(m => new UsageModelResponse(m.Model, m.Tokens, m.EstimatedCost, m.Actors)).ToList(),
                s.ByDay.Select(d => new UsageDayResponse(d.Period, d.Tokens, d.EstimatedCost, d.Actors)).ToList(),
                Forecast(s.Forecast),
                s.ByProvider.Select(p => new UsageProviderResponse(p.Provider, p.Tokens, p.EstimatedCost, p.UnpricedTokens, p.Actors, p.Models)).ToList(),
                s.ByTool.Select(t => new UsageToolResponse(t.Tool, t.Tokens, t.EstimatedCost, t.Actors, t.Source)).ToList(),
                s.ByTeam.Select(t => new TeamSpendResponse(t.Team, t.Members, t.ActiveMembers, t.Tokens, t.EstimatedCost, t.MonthToDateCost, t.ProjectedMonthCost)).ToList()));
        });

        // Today as it happens: per-actor running totals from the report events (the agent's `install --every 5m`
        // or `watch` keep them flowing), an intraday curve and who reported within the last N minutes.
        group.MapGet("/usage/live", async (UsageReportService usage, CancellationToken ct, int activeMinutes = 15) =>
        {
            var live = await usage.LiveAsync(activeMinutes, ct);
            return live is null ? Results.NoContent() : Results.Ok(new LiveUsageResponse(
                live.Period, live.AsOfUtc, live.ActiveMinutes, live.ActiveActors, live.ReportingActorsToday, live.TokensToday, live.EstimatedCostToday, live.TokensLastHour,
                live.Actors.Select(a => new LiveActorResponse(a.Actor, a.Tools, a.LastReportUtc, a.ActiveNow, a.TokensToday, a.EstimatedCostToday, a.RequestsToday, a.TokensLastHour)).ToList(),
                live.Curve.Select(p => new LivePointResponse(p.AtUtc, p.Tokens, p.EstimatedCost)).ToList()));
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

        // OpenTelemetry receiver (OTLP/HTTP JSON). Claude Code, Gemini CLI, instrumented apps and gateways push here;
        // analyst tokens suffice (a developer's own machine reports its own usage). Protobuf is answered with a hint.
        group.MapPost("/otlp/v1/metrics", async (HttpRequest http, TelemetryIngestService telemetry, CancellationToken ct) => await Otlp(http, telemetry, traces: false, ct));
        group.MapPost("/otlp/v1/traces", async (HttpRequest http, TelemetryIngestService telemetry, CancellationToken ct) => await Otlp(http, telemetry, traces: true, ct));

        // Finance export: one CSV line per day/actor/tool/model with the team resolved now. Formula-safe, UTF-8 BOM.
        group.MapGet("/usage/export.csv", async (IUsageFactRepository facts, ITeamRepository teams, CancellationToken ct, int days = 30) =>
        {
            var to = DateOnly.FromDateTime(DateTime.UtcNow);
            var from = to.AddDays(-Math.Clamp(days <= 0 ? 30 : days, 1, UsageReportService.MaxDaysBack));
            var rows = await facts.ListAsync(from, to, ct);
            var csv = UsageCsv.Write(rows, await teams.ListAsync(ct));
            return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", $"atlas-ai-usage-{from:yyyyMMdd}-{to:yyyyMMdd}.csv");
        });

        // The daily billing pull: configuration and last outcome.
        group.MapGet("/cost/schedule", (CostSyncScheduleOptions options, CostSyncScheduleState state) =>
            Results.Ok(new CostSyncScheduleResponse(options.Enabled, options.HourUtc, options.Days, state.NextRunUtc, state.LastRunUtc, state.LastTenants, state.LastSources, state.LastFailures)));

        // Estimated vs billed, per provider, with the calibration ratio the forecasts apply once enough days overlap.
        group.MapGet("/usage/reconciliation", async (ReconciliationService reconciliation, CancellationToken ct, int days = 30) =>
        {
            var r = await reconciliation.BuildAsync(days, ct);
            return Results.Ok(new ReconciliationResponse(r.Days, r.From, r.To,
                r.Providers.Select(p => new ProviderReconciliationResponse(p.Provider, p.Estimated, p.Reported, p.DaysWithBoth, p.DaysEstimatedOnly, p.DaysReportedOnly, p.Ratio, p.RatioUsable, p.EstimatedOnOverlap, p.ReportedOnOverlap, p.Note)).ToList(),
                r.BlendedRatio, r.Method));
        });

        // Budgets: status list, upsert, remove, recent alerts.
        group.MapGet("/budgets", async (BudgetService budgets, CancellationToken ct) =>
            Results.Ok((await budgets.StatusAsync(ct)).Select(BudgetRow).ToList()));
        group.MapPut("/budgets", async (UpsertBudgetRequest request, BudgetService budgets, CancellationToken ct) =>
        {
            try
            {
                if (!Enum.TryParse<BudgetScope>(request.Scope, ignoreCase: true, out var scope))
                {
                    return Results.BadRequest(new { error = $"Unknown scope '{request.Scope}'. Use tenant, team, provider, model, actor or tool." });
                }

                var row = await budgets.UpsertAsync(request.Id, scope, request.ScopeKey, request.MonthlyAmount, request.Name, request.Enabled, request.Author, ct);
                var status = (await budgets.StatusAsync(ct)).First(s => s.Budget.Id == row.Id);
                return Results.Ok(BudgetRow(status));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        group.MapDelete("/budgets/{id:guid}", async (Guid id, BudgetService budgets, CancellationToken ct) =>
            await budgets.RemoveAsync(id, ct) ? Results.NoContent() : Results.NotFound());
        group.MapGet("/budgets/alerts", async (BudgetService budgets, CancellationToken ct, int days = 30) =>
            Results.Ok((await budgets.RecentAlertsAsync(days, ct)).Select(a => new BudgetAlertResponse(a.Id, a.BudgetId, a.Kind, a.Message, a.Amount, a.Percent, a.CreatedAtUtc, a.Delivered, a.DeliveryError)).ToList()));

        // Teams (cost centres): list, upsert, remove, and the actor labels seen so members can be picked.
        group.MapGet("/teams", async (TeamService teams, CancellationToken ct) =>
            Results.Ok((await teams.ListAsync(ct)).Select(TeamRow).ToList()));
        group.MapPut("/teams", async (UpsertTeamRequest request, TeamService teams, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(TeamRow(await teams.UpsertAsync(request.Id, request.Name, request.Members ?? [], request.Author, ct, new AlertChannels(request.WebhookUrl, request.SlackWebhookUrl, request.TeamsWebhookUrl))));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        group.MapDelete("/teams/{id:guid}", async (Guid id, TeamService teams, CancellationToken ct) =>
            await teams.RemoveAsync(id, ct) ? Results.NoContent() : Results.NotFound());
        group.MapGet("/teams/actors", async (TeamService teams, CancellationToken ct) =>
            Results.Ok((await teams.KnownActorsAsync(ct)).Select(a => new KnownActorResponse(a.Actor, a.Team, a.LastSeen)).ToList()));

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
