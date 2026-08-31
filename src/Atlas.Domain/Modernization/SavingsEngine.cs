namespace Atlas.Domain.Modernization;

/// <summary>One annual saving line: what stops being paid once the legacy estate is modernized.</summary>
public sealed record SavingsItem(string Key, decimal AnnualAmount, double Quantity);

/// <summary>
/// The other half of the business case: cost.v1 says what modernization costs, savings.v1 says what
/// staying legacy costs every year. Same philosophy — transparent parameters, explicit assumptions,
/// ranges of confidence left to the reader (these are annual run-rate estimates, not quotes).
/// </summary>
public sealed record SavingsEstimate(
    string ModelVersion,
    IReadOnlyList<SavingsItem> Items,
    decimal AnnualTotal,
    string Currency,
    IReadOnlyList<Assumption> Assumptions);

public static class SavingsEngine
{
    public const string ModelVersion = "savings.v1";

    /// <summary>Null when there is nothing legacy to save on — sections stay hidden instead of showing zeros.</summary>
    public static SavingsEstimate? Estimate(ModernizationProfile profile, CostParameters parameters)
    {
        var legacyApps = profile.LegacyFrameworkProjects;
        if (legacyApps <= 0)
        {
            return null;
        }

        var items = new List<SavingsItem>();
        var assumptions = new List<Assumption>();

        var hosting = legacyApps * parameters.WindowsHostingPerLegacyAppYear;
        if (hosting > 0)
        {
            items.Add(new SavingsItem("saving.windows-hosting", hosting, legacyApps));
            assumptions.Add(new Assumption("assumption.saving-hosting-rate",
                parameters.WindowsHostingPerLegacyAppYear.ToString("0", System.Globalization.CultureInfo.InvariantCulture)));
        }

        var support = legacyApps * parameters.ExtendedSupportPerLegacyAppYear;
        if (support > 0)
        {
            items.Add(new SavingsItem("saving.extended-support", support, legacyApps));
            assumptions.Add(new Assumption("assumption.saving-support-rate",
                parameters.ExtendedSupportPerLegacyAppYear.ToString("0", System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (profile.HasEntityFramework6 && parameters.SqlServerSavingsPerYear > 0)
        {
            items.Add(new SavingsItem("saving.sql-server", parameters.SqlServerSavingsPerYear, 1));
            assumptions.Add(new Assumption("assumption.saving-sql",
                parameters.SqlServerSavingsPerYear.ToString("0", System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (items.Count == 0)
        {
            return null;
        }

        assumptions.Add(new Assumption("assumption.saving-scope", legacyApps.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return new SavingsEstimate(
            ModelVersion,
            items,
            items.Sum(i => i.AnnualAmount),
            parameters.Currency,
            assumptions);
    }
}
