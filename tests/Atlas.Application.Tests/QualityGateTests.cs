using Atlas.Application.Assessments;
using Atlas.Domain.Findings;

namespace Atlas.Application.Tests;

public class QualityGateTests
{
    private static IReadOnlyDictionary<Severity, int> Open(int critical = 0, int high = 0, int medium = 0, int low = 0, int info = 0) =>
        new Dictionary<Severity, int>
        {
            [Severity.Critical] = critical, [Severity.High] = high, [Severity.Medium] = medium, [Severity.Low] = low, [Severity.Informational] = info,
        };

    [Fact]
    public void Passes_when_nothing_is_at_or_above_the_threshold_and_score_is_enough()
    {
        var r = QualityGate.Evaluate(72, Open(medium: 5, low: 40), "High", 60, hasCompletedRun: true);
        Assert.True(r.Passed);
        Assert.True(r.Evaluated);
        Assert.Empty(r.Violations);
        Assert.Equal("High", r.FailOn);
    }

    [Fact]
    public void Fails_on_severity_and_on_score_with_readable_reasons()
    {
        var r = QualityGate.Evaluate(41, Open(critical: 1, high: 3, medium: 9), "high", 60, hasCompletedRun: true);
        Assert.False(r.Passed);
        Assert.Equal(2, r.Violations.Count);
        Assert.Contains("4 open finding(s) at severity High or above: Critical 1, High 3.", r.Violations);
        Assert.Contains("Health score 41 is below the minimum 60.", r.Violations);
    }

    [Fact]
    public void Without_a_completed_run_the_gate_fails_closed_and_unknown_severities_are_rejected()
    {
        var r = QualityGate.Evaluate(null, Open(), "High", null, hasCompletedRun: false);
        Assert.False(r.Passed);
        Assert.False(r.Evaluated);
        Assert.Single(r.Violations);

        Assert.Throws<ArgumentException>(() => QualityGate.Evaluate(90, Open(), "Severe", null, true));
        Assert.Throws<ArgumentException>(() => QualityGate.Evaluate(90, Open(), "7", null, true)); // numeric enum values are typos, not thresholds
        Assert.True(QualityGate.Evaluate(90, Open(high: 1), null, null, true).Passed); // no knobs → pass
        Assert.False(QualityGate.Evaluate(null, Open(), null, 50, true).Passed); // min score without a score
    }

    [Fact]
    public void Baseline_mode_fails_only_on_new_findings_at_or_above_the_threshold()
    {
        // The caller (API) merges created + regressed into this dictionary — reintroduced findings
        // count as "introduced or reintroduced" so a fixed-then-returned Critical blocks the gate.
        var r = QualityGate.Evaluate(80, Open(critical: 3, high: 10), null, null, true, "high",
            new Dictionary<Severity, int> { [Severity.High] = 2, [Severity.Low] = 7 });
        Assert.False(r.Passed);
        Assert.Contains("2 finding(s) introduced or reintroduced by the latest run at severity High or above: High 2.", r.Violations);
        Assert.Equal("High", r.FailOnNew);

        // The existing stock (3 Critical!) does not block when only failOnNew is set and nothing new matches.
        var ok = QualityGate.Evaluate(80, Open(critical: 3), null, null, true, "High",
            new Dictionary<Severity, int> { [Severity.Low] = 4 });
        Assert.True(ok.Passed);

        // First run: the caller passes an empty dictionary — the baseline is established, nothing is new.
        Assert.True(QualityGate.Evaluate(80, Open(critical: 3), null, null, true, "High", new Dictionary<Severity, int>()).Passed);

        // Both knobs combine: failOn still sees the stock.
        Assert.False(QualityGate.Evaluate(80, Open(critical: 3), "Critical", null, true, "High", new Dictionary<Severity, int>()).Passed);

        Assert.Throws<ArgumentException>(() => QualityGate.Evaluate(80, Open(), null, null, true, "Severe", new Dictionary<Severity, int>()));
    }

    [Fact]
    public void Severity_ranking_is_explicit()
    {
        Assert.True(QualityGate.IsAtLeast(Severity.Critical, Severity.High));
        Assert.True(QualityGate.IsAtLeast(Severity.High, Severity.High));
        Assert.False(QualityGate.IsAtLeast(Severity.Medium, Severity.High));
        Assert.True(QualityGate.IsAtLeast(Severity.Low, Severity.Informational));
    }
}
