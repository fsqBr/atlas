namespace Atlas.Domain.Modernization;

/// <summary>
/// The tenant's market parameters for cost.v1: an hourly rate is a market fact, not an FX
/// conversion — a US estate is estimated at US$ rates, not at BRL times a quote. Only the
/// commercial knobs live here; the effort model (hours per KLOC, multipliers) stays global.
/// </summary>
public sealed class TenantCostProfile
{
    public Guid TenantId { get; private set; }
    public string Currency { get; private set; } = null!;
    public decimal HourlyRate { get; private set; }
    public int? TeamSize { get; private set; }

    /// <summary>
    /// Optional per-legacy-app annual savings rates in the tenant's own currency. When set, the
    /// modernization ROI/savings can be computed for a tenant whose currency differs from the
    /// deployment default (whose savings knobs are in the deployment's currency). Null = fall back
    /// to the deployment knobs when the currencies match, otherwise savings stay hidden (never
    /// FX-relabeled).
    /// </summary>
    public decimal? WindowsHostingPerLegacyAppYear { get; private set; }
    public decimal? ExtendedSupportPerLegacyAppYear { get; private set; }
    public decimal? SqlServerSavingsPerYear { get; private set; }

    public string UpdatedBy { get; private set; } = null!;
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    private TenantCostProfile()
    {
    }

    public TenantCostProfile(Guid tenantId, string currency, decimal hourlyRate, int? teamSize, string updatedBy,
        decimal? windowsHostingPerLegacyAppYear = null, decimal? extendedSupportPerLegacyAppYear = null, decimal? sqlServerSavingsPerYear = null)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        TenantId = tenantId;
        Update(currency, hourlyRate, teamSize, updatedBy, windowsHostingPerLegacyAppYear, extendedSupportPerLegacyAppYear, sqlServerSavingsPerYear);
    }

    public void Update(string currency, decimal hourlyRate, int? teamSize, string updatedBy,
        decimal? windowsHostingPerLegacyAppYear = null, decimal? extendedSupportPerLegacyAppYear = null, decimal? sqlServerSavingsPerYear = null)
    {
        currency = currency?.Trim().ToUpperInvariant() ?? string.Empty;
        if (currency.Length is < 3 or > 3 || !currency.All(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException("Currency must be a 3-letter ISO code (BRL, USD, EUR…).", nameof(currency));
        }

        if (hourlyRate is <= 0 or > 100_000)
        {
            throw new ArgumentException("Hourly rate must be between 1 and 100,000.", nameof(hourlyRate));
        }

        if (teamSize is < 1 or > 500)
        {
            throw new ArgumentException("Team size must be between 1 and 500.", nameof(teamSize));
        }

        foreach (var (rate, name) in new[]
                 {
                     (windowsHostingPerLegacyAppYear, "Windows hosting saving"),
                     (extendedSupportPerLegacyAppYear, "Extended support saving"),
                     (sqlServerSavingsPerYear, "SQL Server saving"),
                 })
        {
            if (rate is < 0 or > 10_000_000)
            {
                throw new ArgumentException($"{name} rate must be between 0 and 10,000,000.", nameof(updatedBy));
            }
        }

        Currency = currency;
        HourlyRate = hourlyRate;
        TeamSize = teamSize;
        WindowsHostingPerLegacyAppYear = windowsHostingPerLegacyAppYear;
        ExtendedSupportPerLegacyAppYear = extendedSupportPerLegacyAppYear;
        SqlServerSavingsPerYear = sqlServerSavingsPerYear;
        UpdatedBy = string.IsNullOrWhiteSpace(updatedBy) ? "unknown" : updatedBy.Trim();
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }
}
