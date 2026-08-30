namespace Atlas.Domain.Modernization;

/// <summary>
/// What a modernization actually took, recorded after the fact so cost.v1 can be
/// compared with reality ("calibratable using real project
/// outcomes"). One record per assessment; re-recording replaces it.
/// </summary>
public sealed class ModernizationActual
{
    public Guid AssessmentId { get; private set; }
    public Guid TenantId { get; private set; }
    public ModernizationStrategy Strategy { get; private set; }
    public double ActualHours { get; private set; }
    public double? ActualMonths { get; private set; }
    public decimal? ActualCost { get; private set; }
    public string Currency { get; private set; } = null!;
    public string? Notes { get; private set; }
    public string RecordedBy { get; private set; } = null!;
    public DateTimeOffset RecordedAtUtc { get; private set; }

    private ModernizationActual()
    {
    }

    public ModernizationActual(Guid assessmentId, Guid tenantId, ModernizationStrategy strategy, double actualHours, double? actualMonths, decimal? actualCost, string currency, string? notes, string recordedBy)
    {
        if (assessmentId == Guid.Empty || tenantId == Guid.Empty)
        {
            throw new ArgumentException("Ids must not be empty.");
        }

        AssessmentId = assessmentId;
        TenantId = tenantId;
        Update(strategy, actualHours, actualMonths, actualCost, currency, notes, recordedBy);
    }

    public void Update(ModernizationStrategy strategy, double actualHours, double? actualMonths, decimal? actualCost, string currency, string? notes, string recordedBy)
    {
        if (actualHours <= 0)
        {
            throw new ArgumentException("Actual hours must be positive.", nameof(actualHours));
        }

        if (string.IsNullOrWhiteSpace(recordedBy))
        {
            throw new ArgumentException("Who recorded the outcome is required.", nameof(recordedBy));
        }

        Strategy = strategy;
        ActualHours = actualHours;
        ActualMonths = actualMonths is > 0 ? actualMonths : null;
        ActualCost = actualCost is > 0 ? actualCost : null;
        Currency = string.IsNullOrWhiteSpace(currency) ? "BRL" : currency.Trim().ToUpperInvariant();
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        RecordedBy = recordedBy.Trim();
        RecordedAtUtc = DateTimeOffset.UtcNow;
    }
}

/// <summary>Estimated (likely) vs actual for one assessment; Ratio &gt; 1 means the model under-estimated.</summary>
public sealed record CalibrationPoint(Guid AssessmentId, string AssessmentName, ModernizationStrategy Strategy, double EstimatedLikelyHours, double ActualHours, double Ratio, string? Notes, DateTimeOffset RecordedAtUtc);

public sealed record CalibrationSummary(int Points, double? MeanRatio, double? MedianRatio, string Recommendation, IReadOnlyList<CalibrationPoint> Items)
{
    public static CalibrationSummary From(IReadOnlyList<CalibrationPoint> items)
    {
        if (items.Count == 0)
        {
            return new CalibrationSummary(0, null, null, "calibration.none", items);
        }

        var ratios = items.Select(i => i.Ratio).OrderBy(r => r).ToList();
        var mean = Math.Round(ratios.Average(), 2);
        var median = Math.Round(ratios.Count % 2 == 1 ? ratios[ratios.Count / 2] : (ratios[ratios.Count / 2 - 1] + ratios[ratios.Count / 2]) / 2, 2);
        var recommendation = items.Count < 3 ? "calibration.too-few"
            : median > 1.25 ? "calibration.raise-rates"
            : median < 0.8 ? "calibration.lower-rates"
            : "calibration.ok";
        return new CalibrationSummary(items.Count, mean, median, recommendation, items);
    }
}
