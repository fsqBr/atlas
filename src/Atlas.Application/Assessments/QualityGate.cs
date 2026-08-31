using Atlas.Domain.Findings;

namespace Atlas.Application.Assessments;

public sealed record QualityGateResult(
    bool Passed,
    bool Evaluated,
    int? Score,
    IReadOnlyDictionary<Severity, int> OpenBySeverity,
    IReadOnlyList<string> Violations,
    string? FailOn,
    int? MinScore);

/// <summary>
/// The CI gate ("shift-left"): pass/fail from the latest completed
/// run. Two knobs — fail when any open finding is at or above a severity, and/or
/// when the health score is below a minimum. Pure; the API feeds it snapshots.
/// </summary>
public static class QualityGate
{
    public static QualityGateResult Evaluate(int? score, IReadOnlyDictionary<Severity, int> openBySeverity, string? failOn, int? minScore, bool hasCompletedRun)
    {
        var violations = new List<string>();
        Severity? threshold = null;
        if (!string.IsNullOrWhiteSpace(failOn))
        {
            if (!Enum.TryParse<Severity>(failOn, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed) || char.IsAsciiDigit(failOn.Trim()[0]))
            {
                throw new ArgumentException($"failOn must be one of {string.Join(", ", Enum.GetNames<Severity>())}.", nameof(failOn));
            }

            threshold = parsed;
        }

        if (!hasCompletedRun)
        {
            violations.Add("No completed run to evaluate.");
            return new QualityGateResult(false, false, score, openBySeverity, violations, threshold?.ToString(), minScore);
        }

        if (threshold is { } t)
        {
            // Severity enum: Critical is the highest; "at or above" means numerically >= only if the enum is ordered ascending.
            var offending = openBySeverity.Where(kv => IsAtLeast(kv.Key, t) && kv.Value > 0).OrderByDescending(kv => kv.Key).ToList();
            if (offending.Count > 0)
            {
                violations.Add($"{offending.Sum(kv => kv.Value)} open finding(s) at severity {t} or above: " + string.Join(", ", offending.Select(kv => $"{kv.Key} {kv.Value}")) + ".");
            }
        }

        if (minScore is { } min)
        {
            if (score is null)
            {
                violations.Add($"Health score not available; minimum is {min}.");
            }
            else if (score < min)
            {
                violations.Add($"Health score {score} is below the minimum {min}.");
            }
        }

        return new QualityGateResult(violations.Count == 0, true, score, openBySeverity, violations, threshold?.ToString(), minScore);
    }

    /// <summary>Severity ranking independent of the enum's numeric order.</summary>
    private static readonly Severity[] Ranking = [Severity.Informational, Severity.Low, Severity.Medium, Severity.High, Severity.Critical];

    public static bool IsAtLeast(Severity value, Severity threshold) => Array.IndexOf(Ranking, value) >= Array.IndexOf(Ranking, threshold);
}
