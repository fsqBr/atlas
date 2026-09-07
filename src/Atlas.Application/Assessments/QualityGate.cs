using Atlas.Domain.Findings;

namespace Atlas.Application.Assessments;

public sealed record QualityGateResult(
    bool Passed,
    bool Evaluated,
    int? Score,
    IReadOnlyDictionary<Severity, int> OpenBySeverity,
    IReadOnlyList<string> Violations,
    string? FailOn,
    int? MinScore,
    string? FailOnNew = null,
    IReadOnlyDictionary<Severity, int>? NewBySeverity = null);

/// <summary>
/// The CI gate ("shift-left"): pass/fail from the latest completed
/// run. Three knobs — fail when any open finding is at or above a severity,
/// when the health score is below a minimum, and/or (baseline mode) when the
/// latest run introduced NEW findings at or above a severity. Baseline mode is
/// the adoptable one for legacy estates: the existing stock of findings never
/// blocks, regressions do. Pure; the API feeds it snapshots and the run diff.
/// </summary>
public static class QualityGate
{
    public static QualityGateResult Evaluate(
        int? score,
        IReadOnlyDictionary<Severity, int> openBySeverity,
        string? failOn,
        int? minScore,
        bool hasCompletedRun,
        string? failOnNew = null,
        IReadOnlyDictionary<Severity, int>? newBySeverity = null)
    {
        var violations = new List<string>();
        var threshold = ParseSeverity(failOn, nameof(failOn));
        var newThreshold = ParseSeverity(failOnNew, nameof(failOnNew));

        if (!hasCompletedRun)
        {
            violations.Add("No completed run to evaluate.");
            return new QualityGateResult(false, false, score, openBySeverity, violations, threshold?.ToString(), minScore, newThreshold?.ToString(), newBySeverity);
        }

        if (threshold is { } t)
        {
            var offending = openBySeverity.Where(kv => IsAtLeast(kv.Key, t) && kv.Value > 0).OrderByDescending(kv => kv.Key).ToList();
            if (offending.Count > 0)
            {
                violations.Add($"{offending.Sum(kv => kv.Value)} open finding(s) at severity {t} or above: " + string.Join(", ", offending.Select(kv => $"{kv.Key} {kv.Value}")) + ".");
            }
        }

        // Baseline mode: only what the LATEST run introduced counts, with exact per-finding
        // severity counts (RunDiff.NewBySeverity). A first run (no previous) establishes the
        // baseline: the caller passes an empty dictionary and nothing is "new".
        if (newThreshold is { } nt && newBySeverity is not null)
        {
            var offendingNew = newBySeverity
                .Where(kv => IsAtLeast(kv.Key, nt) && kv.Value > 0)
                .OrderByDescending(kv => Array.IndexOf(Ranking, kv.Key))
                .ToList();
            if (offendingNew.Count > 0)
            {
                violations.Add($"{offendingNew.Sum(kv => kv.Value)} finding(s) introduced or reintroduced by the latest run at severity {nt} or above: " + string.Join(", ", offendingNew.Select(kv => $"{kv.Key} {kv.Value}")) + ".");
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

        return new QualityGateResult(violations.Count == 0, true, score, openBySeverity, violations, threshold?.ToString(), minScore, newThreshold?.ToString(), newBySeverity);
    }

    private static Severity? ParseSeverity(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Enum.TryParse<Severity>(value, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed) || char.IsAsciiDigit(value.Trim()[0]))
        {
            throw new ArgumentException($"{paramName} must be one of {string.Join(", ", Enum.GetNames<Severity>())}.", paramName);
        }

        return parsed;
    }

    /// <summary>Severity ranking independent of the enum's numeric order.</summary>
    private static readonly Severity[] Ranking = [Severity.Informational, Severity.Low, Severity.Medium, Severity.High, Severity.Critical];

    public static bool IsAtLeast(Severity value, Severity threshold) => Array.IndexOf(Ranking, value) >= Array.IndexOf(Ranking, threshold);
}
