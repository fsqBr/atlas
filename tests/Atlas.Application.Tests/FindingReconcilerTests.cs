using Atlas.Application.Findings;
using Atlas.Domain.Findings;
using Atlas.Domain.Rules;
using Atlas.Scanner.Abstractions;

namespace Atlas.Application.Tests;

public class FindingReconcilerTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Assessment = Guid.NewGuid();
    private const string Repo = "github.com/acme/billing";

    private static readonly IReadOnlyDictionary<string, RuleDefinition> Rules = new Dictionary<string, RuleDefinition>
    {
        ["dep.eol"] = new("dep.eol", "dep", "1.0.0", FindingCategory.Modernization, Severity.High, "EOL", "desc", null),
        ["dep.blocker"] = new("dep.blocker", "dep", "1.0.0", FindingCategory.Modernization, Severity.Medium, "Blocker", "desc", null),
    };

    private static FindingCandidate Candidate(string rule, string path, string symbol, int line = 10) => new(
        rule, Severity.High, ConfidenceLevel.High, $"{rule} {symbol}", "message",
        new EvidenceCandidate(FilePath: path, LineStart: line, Symbol: symbol));

    private static ReconciliationResult Run(
        Guid scanId, IReadOnlyList<FindingCandidate> candidates, IReadOnlyList<Finding> existing, bool succeeded = true) =>
        FindingReconciler.Reconcile(Tenant, Assessment, scanId, "dep", "0.1.0", Repo, candidates, Rules, existing, succeeded);

    [Fact]
    public void First_scan_creates_findings_and_occurrences()
    {
        var result = Run(Guid.NewGuid(), [Candidate("dep.eol", "a.csproj", "net45"), Candidate("dep.blocker", "a.csproj", "MB-003")], []);

        Assert.Equal(2, result.Created.Count);
        Assert.Equal(2, result.Occurrences.Count);
        Assert.Equal(0, result.Recurring);
        Assert.Equal(0, result.Resolved);
        Assert.All(result.Created, f => Assert.Equal(FindingStatus.Open, f.Status));
        Assert.All(result.Occurrences, o => Assert.Equal("dep", o.Evidence.ScannerId));
    }

    [Fact]
    public void Second_scan_with_moved_lines_is_recurring_not_new()
    {
        var first = Run(Guid.NewGuid(), [Candidate("dep.eol", "a.csproj", "net45", line: 10)], []);
        var scan2 = Guid.NewGuid();

        var second = Run(scan2, [Candidate("dep.eol", "a.csproj", "net45", line: 42)], first.Created);

        Assert.Empty(second.Created);
        Assert.Equal(1, second.Recurring);
        Assert.Single(second.Occurrences);
        Assert.Equal(scan2, first.Created[0].LastSeenScanId);
    }

    [Fact]
    public void Missing_finding_is_resolved_only_when_scan_succeeded()
    {
        var first = Run(Guid.NewGuid(), [Candidate("dep.eol", "a.csproj", "net45")], []);

        var failedScan = Run(Guid.NewGuid(), [], first.Created, succeeded: false);
        Assert.Equal(0, failedScan.Resolved);
        Assert.Equal(FindingStatus.Open, first.Created[0].Status);

        var okScan = Run(Guid.NewGuid(), [], first.Created);
        Assert.Equal(1, okScan.Resolved);
        Assert.Equal(FindingStatus.Resolved, first.Created[0].Status);
    }

    [Fact]
    public void Resolved_finding_seen_again_is_regressed()
    {
        var first = Run(Guid.NewGuid(), [Candidate("dep.eol", "a.csproj", "net45")], []);
        Run(Guid.NewGuid(), [], first.Created);

        var back = Run(Guid.NewGuid(), [Candidate("dep.eol", "a.csproj", "net45")], first.Created);

        Assert.Equal(1, back.Regressed);
        Assert.Equal(FindingStatus.Regressed, first.Created[0].Status);
    }

    [Fact]
    public void Suppressed_findings_get_occurrences_but_keep_status()
    {
        var first = Run(Guid.NewGuid(), [Candidate("dep.eol", "a.csproj", "net45")], []);
        first.Created[0].Suppress();

        var again = Run(Guid.NewGuid(), [Candidate("dep.eol", "a.csproj", "net45")], first.Created);

        Assert.Single(again.Occurrences);
        Assert.Equal(FindingStatus.Suppressed, first.Created[0].Status);

        var gone = Run(Guid.NewGuid(), [], first.Created);
        Assert.Equal(0, gone.Resolved);
    }

    [Fact]
    public void Duplicate_candidates_in_one_scan_yield_one_finding_two_occurrences()
    {
        var result = Run(Guid.NewGuid(), [Candidate("dep.eol", "a.csproj", "net45"), Candidate("dep.eol", "a.csproj", "net45")], []);

        Assert.Single(result.Created);
        Assert.Equal(2, result.Occurrences.Count);
    }

    [Fact]
    public void Undeclared_rule_is_a_scanner_bug()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Run(Guid.NewGuid(), [Candidate("not.declared", "a", "s")], []));
    }
}
