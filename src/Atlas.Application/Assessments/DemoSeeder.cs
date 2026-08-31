using Atlas.Application.Findings;
using Atlas.Application.Tenants;
using Atlas.Domain.Assessments;
using Atlas.Domain.Findings;
using Atlas.Domain.Scans;
using Atlas.Domain.Sources;
using Atlas.Scanner.Abstractions;

namespace Atlas.Application.Assessments;

/// <summary>
/// One-click demo estate: five entirely fictional assessments with completed runs, real reconciled
/// findings, and backdated health snapshots — dashboards, portfolio, trend, catalog and report all
/// come alive without scanning anything. Everything is marked with the demo:// locator prefix, so
/// removal is one call, and every name, path and message is invented.
/// </summary>
public sealed class DemoSeeder(
    IAssessmentRepository assessments,
    IAssessmentRunRepository runs,
    IScanRepository scans,
    IFindingRepository findings,
    IHealthRepository health,
    IRuleCatalog ruleCatalog,
    ITenantContext tenant,
    IUnitOfWork unitOfWork)
{
    public const string LocatorPrefix = "demo://";
    private const string ScannerId = "demo.core";

    private static readonly IReadOnlyList<RuleSpec> DemoRules =
    [
        new("demo.security.sql-concat", "1.0.0", FindingCategory.Security, Severity.High,
            "SQL built by string concatenation", "User input reaches a SQL string via concatenation.", "Use parameterized queries."),
        new("demo.security.weak-hash", "1.0.0", FindingCategory.Security, Severity.Medium,
            "Weak hash algorithm", "MD5/SHA1 used for security-sensitive hashing.", "Use SHA-256 or a password KDF."),
        new("demo.secrets.connection-string", "1.0.0", FindingCategory.Secrets, Severity.High,
            "Connection string with password in source", "A connection string with credentials is committed.", "Move it to configuration or a secret store."),
        new("demo.dependencies.eol-framework", "1.0.0", FindingCategory.Dependencies, Severity.High,
            "Target framework out of support", "The project targets a framework past end of support.", "Plan the migration to a supported LTS target."),
        new("demo.quality.complexity", "1.0.0", FindingCategory.Quality, Severity.Medium,
            "High cyclomatic complexity", "The method concentrates too many decisions.", "Extract methods and add characterization tests."),
        new("demo.quality.no-tests", "1.0.0", FindingCategory.Quality, Severity.High,
            "Production project without tests", "No test project references this project.", "Start with tests around the most central components."),
        new("demo.architecture.cycle", "1.0.0", FindingCategory.Architecture, Severity.Medium,
            "Namespace dependency cycle", "Namespaces depend on each other cyclically.", "Break the cycle at the least-coupled edge."),
        new("demo.privacy.pii", "1.0.0", FindingCategory.Data, Severity.Medium,
            "Personal data stored", "Contact/personal fields detected in this type.", "Confirm the legal basis and the retention policy."),
        new("demo.modernization.blocker-wcf", "1.0.0", FindingCategory.Modernization, Severity.High,
            "WCF service (no upgrade path)", "WCF hosting has no equivalent on modern .NET.", "Plan CoreWCF or a gRPC/REST redesign."),
    ];

    /// <summary>(name, projectCount, first-run findings, resolved on the second run, days ago of each snapshot)</summary>
    private static readonly IReadOnlyList<(string Name, int Projects, string[] Rules, int ResolveOnSecondRun, int[] SnapshotDaysAgo)> Estate =
    [
        ("Demo — Orion Billing (WebForms)", 14,
            ["demo.security.sql-concat", "demo.security.sql-concat", "demo.secrets.connection-string", "demo.dependencies.eol-framework", "demo.dependencies.eol-framework", "demo.quality.complexity", "demo.quality.complexity", "demo.quality.no-tests", "demo.modernization.blocker-wcf", "demo.privacy.pii"],
            2, [21, 3]),
        ("Demo — Vega Storefront", 9,
            ["demo.security.weak-hash", "demo.dependencies.eol-framework", "demo.quality.complexity", "demo.architecture.cycle", "demo.privacy.pii", "demo.privacy.pii"],
            1, [18, 2]),
        ("Demo — Corvus CRM", 22,
            ["demo.security.sql-concat", "demo.security.weak-hash", "demo.secrets.connection-string", "demo.dependencies.eol-framework", "demo.quality.no-tests", "demo.quality.complexity", "demo.architecture.cycle", "demo.modernization.blocker-wcf"],
            0, [14]),
        ("Demo — Lyra Integrations", 6,
            ["demo.quality.complexity", "demo.architecture.cycle"],
            0, [9]),
        ("Demo — Pavo Reporting", 4,
            ["demo.quality.complexity"],
            0, [4]),
    ];

    private static readonly string[] DemoPaths =
    [
        "src/Billing/InvoiceCalculator.cs", "src/Billing/Data/OrderRepository.cs", "src/Web/Checkout.aspx.cs",
        "src/Core/PricingEngine.cs", "src/Services/CustomerSync.cs", "src/Shared/CryptoHelper.cs",
        "src/Api/Controllers/ReportController.cs", "src/Domain/Customer.cs", "src/Legacy/WcfOrderService.cs",
        "src/Infra/ConnectionFactory.cs",
    ];

    public async Task<int> SeedAsync(CancellationToken cancellationToken)
    {
        var existing = await assessments.ListRecentAsync(10_000, cancellationToken);
        if (existing.Any(a => a.SourceLocator.StartsWith(LocatorPrefix, StringComparison.Ordinal)))
        {
            return 0; // idempotent: the demo estate is already loaded
        }

        var tenantId = tenant.Require();
        var rules = await ruleCatalog.UpsertAsync(ScannerId, DemoRules, cancellationToken);
        var created = 0;
        try
        {

        foreach (var (name, projects, ruleIds, resolveOnSecond, daysAgo) in Estate)
        {
            var slug = new string(name.Where(char.IsAsciiLetter).ToArray()).ToLowerInvariant();
            var assessment = new Assessment(Guid.NewGuid(), tenantId, name, new SourceReference(SourceReference.Kinds.LocalFolder, LocatorPrefix + slug));
            assessments.Add(assessment);
            assessment.Start();

            var firstCandidates = ruleIds.Select((ruleId, i) => Candidate(rules[ruleId], DemoPaths[i % DemoPaths.Length], 20 + i * 17)).ToList();
            var open = await RunOnceAsync(assessment, firstCandidates, rules, cancellationToken);

            if (daysAgo.Length > 1)
            {
                // Second run genuinely resolves some findings: the trend shows real improvement.
                var secondCandidates = firstCandidates.Take(firstCandidates.Count - resolveOnSecond).ToList();
                var open2 = await RunOnceAsync(assessment, secondCandidates, rules, cancellationToken);
                Snapshot(assessment, open, projects, daysAgo[0]);
                Snapshot(assessment, open2, projects, daysAgo[1]);
            }
            else
            {
                Snapshot(assessment, open, projects, daysAgo[0]);
            }

            assessment.Complete(withWarnings: false);
            created++;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // A partial estate would make every retry report "already loaded": roll the demo back.
            await RemoveAsync(CancellationToken.None);
            throw;
        }

        return created;
    }

    public async Task<int> RemoveAsync(CancellationToken cancellationToken)
    {
        // Belt and braces: the demo:// prefix is rejected at creation since v0.42, and removal still
        // double-checks the demo name so a hand-crafted legacy row can never be swept away.
        var demo = (await assessments.ListRecentAsync(10_000, cancellationToken))
            .Where(a => a.SourceLocator.StartsWith(LocatorPrefix, StringComparison.Ordinal)
                && a.Name.StartsWith("Demo — ", StringComparison.Ordinal))
            .ToList();
        foreach (var assessment in demo)
        {
            assessments.Remove(assessment); // dependents cascade
        }

        await ruleCatalog.DeleteScannerRulesAsync(ScannerId, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return demo.Count;
    }

    private async Task<IReadOnlyList<Finding>> RunOnceAsync(
        Assessment assessment,
        IReadOnlyList<FindingCandidate> candidates,
        IReadOnlyDictionary<string, Atlas.Domain.Rules.RuleDefinition> rules,
        CancellationToken cancellationToken)
    {
        var run = new AssessmentRun(Guid.NewGuid(), assessment.TenantId, assessment.Id, await runs.NextNumberAsync(assessment.Id, cancellationToken));
        runs.Add(run);
        var scan = Scan.Start(Guid.NewGuid(), assessment.TenantId, assessment.Id, Guid.Empty, ScannerId, "1.0.0", null, run.Id);
        scans.Add(scan);

        var existing = await findings.GetByAssessmentAndRulesAsync(assessment.Id, rules.Keys.ToList(), cancellationToken);
        var reconciliation = FindingReconciler.Reconcile(
            assessment.TenantId, assessment.Id, scan.Id, ScannerId, "1.0.0",
            assessment.RepositoryKey, candidates, rules, existing, scanSucceeded: true);
        findings.AddRange(reconciliation.Created);
        findings.AddOccurrences(reconciliation.Occurrences);
        scan.Succeed(candidates.Count, reconciliation.Created.Count, reconciliation.Recurring, reconciliation.Resolved, reconciliation.Regressed);
        run.RecordScan(succeeded: true, reconciliation.Created.Count, reconciliation.Recurring, reconciliation.Resolved, reconciliation.Regressed);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var open = await findings.ListOpenAsync(assessment.Id, cancellationToken);
        run.Complete(open.Count, HealthSnapshotFactory.Create(assessment.TenantId, assessment.Id, null, open, 1, null).Score);
        return open;
    }

    private void Snapshot(Assessment assessment, IReadOnlyList<Finding> open, int projects, int daysAgo) =>
        health.Add(HealthSnapshotFactory.Create(
            assessment.TenantId, assessment.Id, null, open, projects, runId: null,
            createdAtUtc: DateTimeOffset.UtcNow.AddDays(-daysAgo)));

    private static FindingCandidate Candidate(Atlas.Domain.Rules.RuleDefinition rule, string path, int line) =>
        new(rule.Id, rule.DefaultSeverity, ConfidenceLevel.High,
            Title: rule.Title,
            Message: rule.Description,
            Evidence: new EvidenceCandidate(FilePath: path, LineStart: line, Symbol: null),
            Remediation: rule.Remediation,
            Data: new Dictionary<string, string> { ["demo"] = "true" });
}
