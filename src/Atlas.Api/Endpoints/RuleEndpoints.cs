using Atlas.Application.Assessments;
using Atlas.Application.Tenants;
using Atlas.Contracts.Assessments;

namespace Atlas.Api.Endpoints;

/// <summary>The rule catalog and the tenant's severity tuning. Extracted from Program.cs (module pattern).</summary>
internal static class RuleEndpoints
{
    public static void Map(WebApplication app)
    {
        // The rule catalog with the tenant's live counts and severity tuning: what Atlas checks, in the open.
        app.MapGet("/api/rules", async (IRuleCatalog ruleCatalog, IAssessmentRepository assessmentsRepo, IFindingRepository findingsRepo, IRuleOverrideRepository ruleOverrides, CancellationToken ct, string? lang = null) =>
        {
            var catalog = await ruleCatalog.GetAllAsync(ct);
            var overrides = (await ruleOverrides.ListAsync(ct)).ToDictionary(o => o.RuleId, o => o.Severity);
            var ids = await assessmentsRepo.ListIdsAsync(ct);
            var open = ids.Count == 0
                ? (IReadOnlyList<Atlas.Application.Portfolio.OpenFindingSummary>)[]
                : await findingsRepo.SummarizeOpenAsync(ids, ct);
            var byRule = open.GroupBy(o => o.RuleId).ToDictionary(
                g => g.Key,
                g => (Count: g.Sum(o => o.Count), Assessments: g.Select(o => o.AssessmentId).Distinct().Count()));

            var pt = string.Equals(lang, "pt", StringComparison.OrdinalIgnoreCase) || string.Equals(lang, "pt-BR", StringComparison.OrdinalIgnoreCase);
            var entries = catalog.Values
                .Select(rule =>
                {
                    var loc = pt ? Atlas.Api.RuleTexts.Localize(rule, "pt-BR") : null;
                    var counts = byRule.GetValueOrDefault(rule.Id);
                    return new RuleCatalogEntryResponse(
                        rule.Id, rule.ScannerId, rule.Category.ToString(), rule.DefaultSeverity.ToString(),
                        overrides.TryGetValue(rule.Id, out var tuned) ? tuned.ToString() : null,
                        loc?.Title ?? rule.Title, loc?.Description ?? rule.Description, loc?.Remediation ?? rule.Remediation,
                        counts.Count, counts.Assessments);
                })
                .OrderBy(r => r.Category, StringComparer.Ordinal)
                .ThenBy(r => r.Id, StringComparer.Ordinal)
                .ToList();
            return Results.Ok(entries);
        }).RequireRateLimiting("api");

        // Tenant severity tuning: null severity restores the catalog default. Applies from the next run.
        app.MapPut("/api/rules/{ruleId}/severity", async (string ruleId, RuleSeverityRequest request, IRuleCatalog ruleCatalog, IRuleOverrideRepository ruleOverrides, Atlas.Application.Tenants.ITenantContext tenant, IUnitOfWork unitOfWork, CancellationToken ct) =>
        {
            var catalog = await ruleCatalog.GetAllAsync(ct);
            if (!catalog.TryGetValue(ruleId, out var rule))
            {
                return Results.NotFound();
            }

            var existing = await ruleOverrides.GetAsync(ruleId, ct);
            if (string.IsNullOrWhiteSpace(request.Severity))
            {
                if (existing is not null)
                {
                    ruleOverrides.Remove(existing);
                    try
                    {
                        await unitOfWork.SaveChangesAsync(ct);
                    }
                    catch (Microsoft.EntityFrameworkCore.DbUpdateException)
                    {
                        // already removed concurrently: the desired state (no override) holds
                    }
                }

                return Results.Ok(new { ruleId, severity = (string?)null });
            }

            if (!Enum.TryParse<Atlas.Domain.Findings.Severity>(request.Severity, true, out var severity) || !Enum.IsDefined(severity) || char.IsAsciiDigit(request.Severity.Trim()[0]))
            {
                return Results.BadRequest(new { error = $"Severity must be one of {string.Join(", ", Enum.GetNames<Atlas.Domain.Findings.Severity>())}." });
            }

            var author = string.IsNullOrWhiteSpace(request.Author) ? tenant.SubjectName ?? tenant.Subject ?? "unknown" : request.Author;
            if (existing is null)
            {
                ruleOverrides.Add(new Atlas.Domain.Rules.RuleSeverityOverride(tenant.Require(), ruleId, severity, author));
            }
            else
            {
                existing.Update(severity, author);
            }

            try
            {
                await unitOfWork.SaveChangesAsync(ct);
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException)
            {
                return Results.Conflict(new { error = "The rule was tuned concurrently; reload and try again." });
            }

            return Results.Ok(new { ruleId, severity = severity.ToString() });
        }).RequireRateLimiting("api");
    }
}
