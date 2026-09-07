using Atlas.Application.Tenants;
using Atlas.Governance.Domain;
using Microsoft.Extensions.Logging;

namespace Atlas.Governance.Application;

/// <summary>One effective price line: a tenant override or a builtin entry, in catalog order.</summary>
public sealed record EffectivePrice(Guid? Id, string Pattern, decimal Input, decimal Output, decimal? CacheRead, decimal? CacheWrite, string Source, string? Note, string? UpdatedBy, DateTimeOffset? UpdatedAtUtc);

public sealed record EffectivePriceList(string Version, string BaseVersion, string Currency, string? Note, IReadOnlyList<EffectivePrice> Prices, IReadOnlyList<UnpricedModel> Unpriced);

/// <summary>A model seen in usage reports that no price line matches (a ready-made suggestion for the editor).</summary>
public sealed record UnpricedModel(string Model, long Tokens, int Actors, DateOnly LastSeen);

/// <summary>Resolves the tenant's effective price catalog: overrides first, then the builtin (or configured) list.</summary>
public interface IPriceCatalogResolver
{
    Task<PriceCatalog> ResolveAsync(CancellationToken cancellationToken);
}

public sealed class PriceCatalogResolver(PriceCatalog baseCatalog, IModelPriceRepository overrides) : IPriceCatalogResolver
{
    public async Task<PriceCatalog> ResolveAsync(CancellationToken cancellationToken)
    {
        var rows = await overrides.ListAsync(cancellationToken);
        return rows.Count == 0
            ? baseCatalog
            : baseCatalog.WithOverrides(rows.Select(r => new ModelPrice(r.Pattern, r.Input, r.Output, r.CacheRead, r.CacheWrite)), $"{baseCatalog.Version}+tenant");
    }
}

/// <summary>Edits tenant model prices and reprices the stored usage estimates after every change.</summary>
public sealed class ModelPriceService(
    IModelPriceRepository overrides,
    IUsageFactRepository facts,
    PriceCatalog baseCatalog,
    IPriceCatalogResolver resolver,
    IGovernanceUnitOfWork unitOfWork,
    ITenantContext tenant,
    ILogger<ModelPriceService> logger)
{
    public const int MaxOverrides = 200;

    public async Task<EffectivePriceList> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await overrides.ListAsync(cancellationToken);
        var effective = await resolver.ResolveAsync(cancellationToken);
        var lines = new List<EffectivePrice>();
        lines.AddRange(rows.Select(r => new EffectivePrice(r.Id, r.Pattern, r.Input, r.Output, r.CacheRead, r.CacheWrite, "tenant", r.Note, r.UpdatedBy, r.UpdatedAtUtc)));
        lines.AddRange(baseCatalog.Document.Models.Select(m => new EffectivePrice(null, m.Pattern, m.Input, m.Output, m.CacheRead, m.CacheWrite, baseCatalog.Source == "builtin" ? "builtin" : "config", null, null, null)));

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var recent = await facts.ListAsync(today.AddDays(-UsageReportService.MaxDaysBack), today, cancellationToken);
        var unpriced = recent
            .Where(f => effective.Find(f.Model) is null)
            .GroupBy(f => f.Model, StringComparer.OrdinalIgnoreCase)
            .Select(g => new UnpricedModel(g.Key, g.Sum(f => f.TotalTokens), g.Select(f => f.Actor).Distinct(StringComparer.Ordinal).Count(), g.Max(f => f.Period)))
            .OrderByDescending(u => u.Tokens)
            .ToList();

        return new EffectivePriceList(effective.Version, baseCatalog.Version, baseCatalog.Currency, baseCatalog.Document.Note, lines, unpriced);
    }

    /// <summary>Creates or updates the override with this pattern, then reprices. Returns the stored row.</summary>
    public async Task<ModelPriceOverride> UpsertAsync(string pattern, decimal input, decimal output, decimal? cacheRead, decimal? cacheWrite, string? note, string? updatedBy, CancellationToken cancellationToken)
    {
        var normalized = ModelPriceOverride.Normalize(pattern);
        var rows = await overrides.ListAsync(cancellationToken);
        var existing = rows.FirstOrDefault(r => string.Equals(r.Pattern, normalized, StringComparison.Ordinal));
        if (existing is null)
        {
            if (rows.Count >= MaxOverrides)
            {
                throw new ArgumentException($"At most {MaxOverrides} model prices per tenant.");
            }

            existing = new ModelPriceOverride(Guid.NewGuid(), tenant.Require(), normalized, input, output, cacheRead, cacheWrite, note, updatedBy ?? "web");
            overrides.Add(existing);
            rows = [.. rows, existing];
        }
        else
        {
            existing.Set(input, output, cacheRead, cacheWrite, note, updatedBy ?? "web");
        }

        var repriced = await RepriceAsync(rows, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Model price '{Pattern}' saved by {By}; {Count} usage line(s) repriced.", normalized, existing.UpdatedBy, repriced);
        return existing;
    }

    public async Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        var rows = await overrides.ListAsync(cancellationToken);
        var row = rows.FirstOrDefault(r => r.Id == id);
        if (row is null)
        {
            return false;
        }

        overrides.Remove(row);
        var repriced = await RepriceAsync(rows.Where(r => r.Id != id).ToList(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Model price '{Pattern}' removed; {Count} usage line(s) repriced.", row.Pattern, repriced);
        return true;
    }

    /// <summary>
    /// Recomputes every stored estimate with the catalog as it will stand after this change. The pending rows are
    /// passed in: a query through the repository would still see the database state before SaveChanges.
    /// </summary>
    private async Task<int> RepriceAsync(IReadOnlyList<ModelPriceOverride> pending, CancellationToken cancellationToken)
    {
        var catalog = pending.Count == 0
            ? baseCatalog
            : baseCatalog.WithOverrides(pending.Select(r => new ModelPrice(r.Pattern, r.Input, r.Output, r.CacheRead, r.CacheWrite)), $"{baseCatalog.Version}+tenant");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var all = await facts.ListAsync(today.AddDays(-UsageReportService.MaxDaysBack), today.AddDays(1), cancellationToken);
        var changed = 0;
        foreach (var f in all)
        {
            var estimate = catalog.Estimate(f.Model, f.InputTokens, f.OutputTokens, f.CacheReadTokens, f.CacheWriteTokens);
            if (f.Reprice(estimate, catalog.Version))
            {
                changed++;
            }
        }

        return changed;
    }
}
