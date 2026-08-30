using Atlas.Application.Assessments;
using Atlas.Domain.Assessments;
using Atlas.Domain.Findings;
using Atlas.Domain.Health;
using Atlas.Domain.Rules;

namespace Atlas.Application.Tests;

public class RunDiffTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Assessment = Guid.NewGuid();
    private static readonly Guid PrevScan = Guid.NewGuid();
    private static readonly Guid CurScan = Guid.NewGuid();

    private static readonly IReadOnlyDictionary<string, RuleDefinition> Rules = new Dictionary<string, RuleDefinition>
    {
        ["mb"] = new("mb", "dep", "1.0.0", FindingCategory.Modernization, Severity.High, "Migration blocker", "d", null),
        ["sql"] = new("sql", "sec", "1.0.0", FindingCategory.Security, Severity.High, "Dynamic SQL", "d", null),
    };

    private static AssessmentRun Run(int number, int? score, string? commit = "abc")
    {
        var run = new AssessmentRun(Guid.NewGuid(), Tenant, Assessment, number);
        run.SetCommit(commit);
        if (score is { } s)
        {
            run.RecordScan(true, 1, 1, 1, 0);
            run.Complete(10, s);
        }

        return run;
    }

    private static FindingWithLatestOccurrence Finding(string rule, Guid firstSeen, Guid lastSeen, Guid? resolvedIn, string path, bool regressed = false)
    {
        var f = Domain.Findings.Finding.Create(Guid.NewGuid(), Tenant, Assessment, Guid.NewGuid().ToString("N"), rule,
            Rules[rule].Category, Severity.High, "t", FindingOrigin.Deterministic, firstSeen);
        if (resolvedIn is { } r)
        {
            f.TryResolve(r);
        }
        else if (lastSeen != firstSeen)
        {
            if (regressed)
            {
                f.TryResolve(PrevScan);
            }

            f.Seen(lastSeen, Severity.High, "t");
        }

        var occurrence = new FindingOccurrence(Guid.NewGuid(), Tenant, f.Id, lastSeen, Severity.High, ConfidenceLevel.High, "m", null,
            new Evidence("s", "1", path, 10), null);
        return new FindingWithLatestOccurrence(f, occurrence);
    }

    [Fact]
    public void Classifies_new_resolved_and_regressed_by_scan_membership()
    {
        var previous = Run(1, 62);
        var current = Run(2, 69);
        var touched = new List<FindingWithLatestOccurrence>
        {
            Finding("mb", PrevScan, PrevScan, resolvedIn: CurScan, path: "a.csproj"),           // resolved now
            Finding("mb", PrevScan, PrevScan, resolvedIn: CurScan, path: "b.csproj"),           // resolved now
            Finding("sql", CurScan, CurScan, resolvedIn: null, path: "Repo.cs"),                // new now
            Finding("sql", PrevScan, CurScan, resolvedIn: null, path: "Old.cs", regressed: true), // regressed now
            Finding("sql", PrevScan, CurScan, resolvedIn: null, path: "Same.cs"),               // recurring: ignored
        };

        var result = RunDiff.Compute(current, previous, [CurScan], touched,
            [new HealthDimension("Security", 0.3, 40, 60, []), new HealthDimension("Modernization", 0.25, 80, 20, [])],
            [new HealthDimension("Security", 0.3, 50, 50, []), new HealthDimension("Modernization", 0.25, 60, 40, [])],
            [], [], Rules);

        Assert.Equal(7, result.HealthDelta);
        Assert.False(result.SameCommit == false && previous.CommitSha == current.CommitSha); // same commit "abc"
        Assert.True(result.SameCommit);

        var resolved = Assert.Single(result.Resolved);
        Assert.Equal("Migration blocker", resolved.Title);
        Assert.Equal(2, resolved.Count);
        Assert.Equal(["a.csproj:10", "b.csproj:10"], resolved.SampleLocations);

        var created = Assert.Single(result.New);
        Assert.Equal("sql", created.RuleId);
        Assert.Equal(1, created.Count);

        var regressed = Assert.Single(result.Regressed);
        Assert.Equal("Old.cs:10", regressed.SampleLocations[0]);

        Assert.Equal(-10, result.Dimensions.Single(d => d.Name == "Security").Delta);
        Assert.Equal(20, result.Dimensions.Single(d => d.Name == "Modernization").Delta);
    }

    [Fact]
    public void First_run_has_no_previous_and_no_deltas()
    {
        var current = Run(1, 55, commit: null);

        var result = RunDiff.Compute(current, null, [CurScan], [], [new HealthDimension("Quality", 0.15, 55, 45, [])], null, [], [], Rules);

        Assert.Null(result.Previous);
        Assert.Null(result.HealthDelta);
        Assert.False(result.SameCommit);
        Assert.Null(result.Dimensions.Single().Before);
        Assert.Null(result.Dimensions.Single().Delta);
        Assert.Null(result.Inventory);
    }

    [Fact]
    public void Inventory_delta_sums_across_languages()
    {
        var previous = Run(1, 50);
        var current = Run(2, 50);
        InventorySnapshot Snap(long lines, int files, int projects) => new(
            Guid.NewGuid(), Tenant, Assessment, Guid.NewGuid(), "c", "csharp", "Syntactic", files, lines, 1, 1, 1, 1.0, null, projects, 1, "[]");

        var result = RunDiff.Compute(current, previous, [CurScan], [], [], [], [Snap(1200, 30, 3)], [Snap(1000, 25, 3)], Rules);

        Assert.NotNull(result.Inventory);
        Assert.Equal(1000, result.Inventory.LinesBefore);
        Assert.Equal(1200, result.Inventory.LinesAfter);
        Assert.Equal(25, result.Inventory.FilesBefore);
        Assert.Equal(3, result.Inventory.ProjectsAfter);
    }
}
