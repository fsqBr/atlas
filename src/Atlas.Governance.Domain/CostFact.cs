namespace Atlas.Governance.Domain;

/// <summary>
/// Where a number came from. Facts of different bases are never summed without a label:
/// a provider invoice line and an Atlas estimate are different claims about money.
/// </summary>
public enum CostBasis
{
    /// <summary>Read from the provider's billing/cost API.</summary>
    ProviderReported = 1,

    /// <summary>Computed by Atlas from usage × a versioned price catalog (e.g. Copilot seats × list price, Claude Code estimated cost).</summary>
    Estimated = 2,
}

/// <summary>
/// One day of spend (or usage) for one provider at one grain — the atom every cost view is built from.
/// Idempotent by construction: a sync replaces the facts of its source inside the collected window.
/// </summary>
public sealed class CostFact
{
    private CostFact()
    {
    }

    public CostFact(
        Guid id,
        Guid tenantId,
        Guid sourceId,
        string provider,
        CostBasis basis,
        DateOnly period,
        string dimension,
        string dimensionKey,
        string? detail,
        decimal amount,
        string currency,
        decimal? quantity,
        string? unit,
        string priceCatalogVersion)
    {
        if (string.IsNullOrWhiteSpace(dimension) || string.IsNullOrWhiteSpace(dimensionKey))
        {
            throw new ArgumentException("Dimension and key are required.");
        }

        if (string.IsNullOrWhiteSpace(currency))
        {
            throw new ArgumentException("Currency is required.", nameof(currency));
        }

        Id = id;
        TenantId = tenantId;
        SourceId = sourceId;
        Provider = CostProviders.Normalize(provider);
        Basis = basis;
        Period = period;
        Dimension = dimension.Trim();
        DimensionKey = Truncate(dimensionKey.Trim(), 200);
        Detail = detail is null ? null : Truncate(detail.Trim(), 200);
        Amount = amount;
        Currency = currency.Trim().ToUpperInvariant();
        Quantity = quantity;
        Unit = unit;
        PriceCatalogVersion = priceCatalogVersion;
        CollectedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid SourceId { get; private set; }

    public string Provider { get; private set; } = null!;

    public CostBasis Basis { get; private set; }

    /// <summary>UTC day the fact belongs to.</summary>
    public DateOnly Period { get; private set; }

    /// <summary>project | workspace | seats | claude-code | line-item …</summary>
    public string Dimension { get; private set; } = null!;

    public string DimensionKey { get; private set; } = null!;

    /// <summary>Finer label inside the grain: line item, model, description.</summary>
    public string? Detail { get; private set; }

    public decimal Amount { get; private set; }

    /// <summary>ISO 4217, upper case.</summary>
    public string Currency { get; private set; } = null!;

    /// <summary>Usage behind the amount when known (tokens, seats, sessions).</summary>
    public decimal? Quantity { get; private set; }

    public string? Unit { get; private set; }

    /// <summary>Which price list produced an Estimated amount, or the provider API version for reported ones.</summary>
    public string PriceCatalogVersion { get; private set; } = null!;

    public DateTimeOffset CollectedAtUtc { get; private set; }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
