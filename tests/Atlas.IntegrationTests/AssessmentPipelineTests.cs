using Atlas.Application.Assessments;
using Atlas.Domain.Assessments;
using Atlas.Domain.Findings;
using Atlas.Domain.Jobs;
using Atlas.Domain.Scans;
using Atlas.Domain.Sources;
using Atlas.Scanner.Dependencies;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.IntegrationTests;

/// <summary>
/// The first true vertical slice: create assessment → job queued → worker-style
/// claim → run → findings persisted → re-run reconciles (recurring / resolved).
/// </summary>
public sealed class AssessmentPipelineTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly string _source = Directory.CreateTempSubdirectory("atlas-it-src").FullName;

    [Fact]
    public async Task Create_run_and_rerun_reconciles_findings_end_to_end()
    {
        WriteLegacyProject(withSystemWeb: true);
        await using var provider = fixture.BuildServices();

        // 1. API-side: create + enqueue.
        Guid assessmentId;
        using (var scope = provider.CreateScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateAssessmentHandler>();
            var created = await handler.HandleAsync(
                "Legacy estate", new SourceReference(SourceReference.Kinds.LocalFolder, _source), CancellationToken.None);
            assessmentId = created.AssessmentId;
        }

        // 2. Worker-side: claim and run.
        await ClaimAndRunAsync(provider);

        using (var scope = provider.CreateScope())
        {
            var assessment = await scope.ServiceProvider.GetRequiredService<IAssessmentRepository>()
                .GetAsync(assessmentId, CancellationToken.None);
            Assert.Equal(AssessmentStatus.Completed, assessment!.Status);

            var scans = await scope.ServiceProvider.GetRequiredService<IScanRepository>()
                .ListByAssessmentAsync(assessmentId, CancellationToken.None);
            var scannerCount = provider.GetServices<Atlas.Scanner.Abstractions.IScanner>().Count();
            Assert.Equal(scannerCount, scans.Count);
            Assert.All(scans, s => Assert.Equal(ScanStatus.Succeeded, s.Status));
            var scan = Assert.Single(scans, s => s.ScannerId == "dependency.nuget");
            Assert.True(scan.FindingsNew >= 4, $"expected >=4 new findings, got {scan.FindingsNew}");

            var page = await scope.ServiceProvider.GetRequiredService<IFindingRepository>()
                .ListAsync(assessmentId, 0, 100, CancellationToken.None);
            var ruleIds = page.Items.Select(i => i.Finding.RuleId).ToHashSet();
            Assert.Contains(DependencyScanner.RuleIds.FrameworkEndOfLife, ruleIds);
            Assert.Contains(ruleIds, r => r.StartsWith(DependencyScanner.RuleIds.MigrationBlockerPrefix));
            Assert.All(page.Items, i => Assert.NotNull(i.Latest));
            Assert.Contains(page.Items, i => i.Latest!.Evidence.Symbol == "MB-003");
            Assert.All(page.Items, i => Assert.Equal(FindingStatus.Open, i.Finding.Status));

            var filtered = await scope.ServiceProvider.GetRequiredService<IFindingRepository>()
                .ListAsync(assessmentId, 0, 100, CancellationToken.None,
                    new FindingFilter(Severity: Severity.High, Search: "System.Web"));
            Assert.True(filtered.Total >= 1);
            Assert.All(filtered.Items, i => Assert.Equal(Severity.High, i.Finding.Severity));

            // Inventory snapshot persisted and the executive report renders from persisted facts only.
            var inventory = await scope.ServiceProvider.GetRequiredService<IInventoryRepository>()
                .GetLatestByAssessmentAsync(assessmentId, CancellationToken.None);
            var csharp = Assert.Single(inventory);
            Assert.Equal(1, csharp.ProjectCount);
            Assert.Equal("SyntacticWithSymbols", csharp.TierAchieved);

            var report = await scope.ServiceProvider.GetRequiredService<Atlas.Reporting.ExecutiveReportBuilder>()
                .BuildAsync(assessmentId, CancellationToken.None);
            var html = Atlas.Reporting.HtmlReportRenderer.Render(report!);
            Assert.Contains("Atlas Test", html);
            Assert.Contains("Legacy.csproj", html);
            Assert.Contains("v4.5", html);
            Assert.Contains("Legacy (non-SDK) project format", html);
            Assert.Contains("Syntactic with symbols (no build)", html);

            var healthSnapshot = await scope.ServiceProvider.GetRequiredService<IHealthRepository>()
                .GetLatestAsync(assessmentId, CancellationToken.None);
            Assert.NotNull(healthSnapshot);
            Assert.Equal("health.v1", healthSnapshot.ModelVersion);
            Assert.InRange(healthSnapshot.Score, 0, 99);
            Assert.True(healthSnapshot.OpenFindings >= 4);
            Assert.Contains("<span class=\"score-v\">" + healthSnapshot.Score + "</span>", html);
        }

        // 3. Fix one blocker, re-run: MB-003 resolves, the rest recur, nothing new.
        //    Also plant a finding under a rule the dependency scanner no longer declares:
        //    retired rules must resolve, not linger (coverage-aware reconciliation).
        WriteLegacyProject(withSystemWeb: false);
        Guid retiredFindingId;
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Atlas.Infrastructure.Persistence.AtlasDbContext>();
            db.RuleDefinitions.Add(new Domain.Rules.RuleDefinition(
                "dependency.retired-rule", "dependency.nuget", "1.0.0", FindingCategory.Dependencies, Severity.Low, "Retired", "d", null));
            var retired = Finding.Create(Guid.NewGuid(), Domain.Tenants.WellKnownTenants.DefaultId, assessmentId, "retired-fp",
                "dependency.retired-rule", FindingCategory.Dependencies, Severity.Low, "Retired finding", FindingOrigin.Deterministic,
                (await scope.ServiceProvider.GetRequiredService<IScanRepository>().ListByAssessmentAsync(assessmentId, CancellationToken.None))[0].Id);
            db.Findings.Add(retired);
            retiredFindingId = retired.Id;

            var queue = scope.ServiceProvider.GetRequiredService<IScanJobQueue>();
            queue.Enqueue(new ScanJob(Guid.NewGuid(), Domain.Tenants.WellKnownTenants.DefaultId, assessmentId));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(CancellationToken.None);
        }

        await ClaimAndRunAsync(provider);

        using (var scope = provider.CreateScope())
        {
            var scans = await scope.ServiceProvider.GetRequiredService<IScanRepository>()
                .ListByAssessmentAsync(assessmentId, CancellationToken.None);
            Assert.Equal(provider.GetServices<Atlas.Scanner.Abstractions.IScanner>().Count() * 2, scans.Count);
            var rerun = scans.Where(s => s.ScannerId == "dependency.nuget").OrderBy(s => s.StartedAtUtc).Last();
            Assert.Equal(0, rerun.FindingsNew);
            Assert.Equal(2, rerun.FindingsResolved); // MB-003 + the retired-rule finding
            Assert.True(rerun.FindingsRecurring >= 3);

            var page = await scope.ServiceProvider.GetRequiredService<IFindingRepository>()
                .ListAsync(assessmentId, 0, 100, CancellationToken.None);
            var webForms = Assert.Single(page.Items, i => i.Latest?.Evidence.Symbol == "MB-003");
            Assert.Equal(FindingStatus.Resolved, webForms.Finding.Status);
            Assert.Equal(FindingStatus.Resolved, page.Items.Single(i => i.Finding.Id == retiredFindingId).Finding.Status);

            // Runs are numbered versions; the comparison explains what changed between them.
            var runs = await scope.ServiceProvider.GetRequiredService<IAssessmentRunRepository>()
                .ListByAssessmentAsync(assessmentId, CancellationToken.None);
            Assert.Equal([2, 1], runs.Select(r => r.Number).ToList());
            Assert.All(runs, r => Assert.Equal(AssessmentRunStatus.Completed, r.Status));
            Assert.Equal(2, runs[0].FindingsResolved);
            Assert.True(runs[0].HealthScore > runs[1].HealthScore, "removing a High blocker must raise the score");

            var comparison = await scope.ServiceProvider.GetRequiredService<RunComparisonBuilder>()
                .BuildAsync(assessmentId, runs[0].Id, null, CancellationToken.None);
            Assert.NotNull(comparison);
            Assert.Equal(1, comparison.Previous!.Number);
            Assert.True(comparison.HealthDelta > 0);
            Assert.Equal(2, comparison.Resolved.Count);
            Assert.Contains(comparison.Resolved, r => r.RuleId == DependencyScanner.RuleIds.MigrationBlocker("MB-003"));
            Assert.Contains(comparison.Resolved, r => r.RuleId == "dependency.retired-rule");
            Assert.Empty(comparison.New);
            Assert.Empty(comparison.Regressed);
            Assert.True(comparison.Dimensions.Single(d => d.Name == "Modernization").Delta > 0);
            Assert.NotNull(comparison.Inventory);

            // Run again is refused while a job is queued.
            var runAgain = scope.ServiceProvider.GetRequiredService<RunAgainHandler>();
            var queued = await runAgain.HandleAsync(assessmentId, CancellationToken.None);
            Assert.NotEqual(Guid.Empty, queued);
            await Assert.ThrowsAsync<InvalidOperationException>(() => runAgain.HandleAsync(assessmentId, CancellationToken.None));

            // The UI needs to see the pending job before the worker picks it up.
            var states = await scope.ServiceProvider.GetRequiredService<IScanJobQueue>()
                .GetActiveJobStatesAsync([assessmentId], CancellationToken.None);
            Assert.Equal(ScanJobState.Queued, states[assessmentId]);
        }

        // Triage: marking a High finding as false positive is auditable, sticky, and recomputes the score right away.
        using (var scope = provider.CreateScope())
        {
            var findingsRepo = scope.ServiceProvider.GetRequiredService<IFindingRepository>();
            var high = (await findingsRepo.ListAsync(assessmentId, 0, 100, CancellationToken.None,
                    new FindingFilter(Severity: Severity.High, Status: FindingStatus.Open))).Items.First().Finding;
            var before = (await scope.ServiceProvider.GetRequiredService<IHealthRepository>().GetLatestAsync(assessmentId, CancellationToken.None))!.Score;

            var triage = scope.ServiceProvider.GetRequiredService<Atlas.Application.Findings.TriageFindingHandler>();
            await Assert.ThrowsAsync<ArgumentException>(() =>
                triage.HandleAsync(assessmentId, high.Id, Atlas.Application.Findings.TriageAction.FalsePositive, "", "Ana", CancellationToken.None));

            var updated = await triage.HandleAsync(assessmentId, high.Id, Atlas.Application.Findings.TriageAction.FalsePositive, "fixture", "Ana", CancellationToken.None);
            Assert.Equal(FindingStatus.FalsePositive, updated.Status);

            var suppression = await scope.ServiceProvider.GetRequiredService<ISuppressionRepository>().GetActiveAsync(high.Id, CancellationToken.None);
            Assert.NotNull(suppression);
            Assert.Equal("Ana", suppression.Author);

            var after = await scope.ServiceProvider.GetRequiredService<IHealthRepository>().GetLatestAsync(assessmentId, CancellationToken.None);
            Assert.Null(after!.RunId); // triage snapshot, not a run
            Assert.True(after.Score > before, $"score should rise after removing a High finding ({before} -> {after.Score})");

            await triage.HandleAsync(assessmentId, high.Id, Atlas.Application.Findings.TriageAction.Reopen, null, "Bruno", CancellationToken.None);
            Assert.Equal(FindingStatus.Open, (await findingsRepo.GetAsync(high.Id, CancellationToken.None))!.Status);
            Assert.Null(await scope.ServiceProvider.GetRequiredService<ISuppressionRepository>().GetActiveAsync(high.Id, CancellationToken.None));
        }

        // Consume the queued job (run #3) so other tests sharing the database see an empty queue.
        await ClaimAndRunAsync(provider);
        using (var scope = provider.CreateScope())
        {
            var runs = await scope.ServiceProvider.GetRequiredService<IAssessmentRunRepository>()
                .ListByAssessmentAsync(assessmentId, CancellationToken.None);
            Assert.Equal(3, runs[0].Number);
            Assert.Equal(0, runs[0].FindingsNew + runs[0].FindingsResolved + runs[0].FindingsRegressed);

            var idle = await scope.ServiceProvider.GetRequiredService<IScanJobQueue>()
                .GetActiveJobStatesAsync([assessmentId], CancellationToken.None);
            Assert.Empty(idle);
        }
    }

    [Fact]
    public async Task Job_claim_is_exclusive_across_claimers()
    {
        await using var provider = fixture.BuildServices();
        Guid assessmentId;

        using (var scope = provider.CreateScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateAssessmentHandler>();
            assessmentId = (await handler.HandleAsync(
                "Claim test", new SourceReference(SourceReference.Kinds.LocalFolder, _source), CancellationToken.None)).AssessmentId;
        }

        using var scopeA = provider.CreateScope();
        using var scopeB = provider.CreateScope();
        var first = await scopeA.ServiceProvider.GetRequiredService<IScanJobQueue>()
            .ClaimAsync("worker-a", TimeSpan.FromMinutes(5), CancellationToken.None);
        var second = await scopeB.ServiceProvider.GetRequiredService<IScanJobQueue>()
            .ClaimAsync("worker-b", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(assessmentId, first.AssessmentId);
        Assert.Equal(ScanJobState.Leased, first.State);
        Assert.Equal("worker-a", first.LeasedBy);
        Assert.Null(second);
    }

    private static async Task ClaimAndRunAsync(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IScanJobQueue>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var job = await queue.ClaimAsync("test-worker", TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(job);
        job.Start();
        await unitOfWork.SaveChangesAsync(CancellationToken.None);

        await scope.ServiceProvider.GetRequiredService<AssessmentRunner>().RunAsync(job.AssessmentId, CancellationToken.None);

        job.Succeed();
        await unitOfWork.SaveChangesAsync(CancellationToken.None);
    }

    private void WriteLegacyProject(bool withSystemWeb)
    {
        var dir = Path.Combine(_source, "Legacy");
        Directory.CreateDirectory(dir);

        var systemWeb = withSystemWeb ? """<Reference Include="System.Web" />""" : string.Empty;
        File.WriteAllText(Path.Combine(dir, "Legacy.csproj"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="12.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <TargetFrameworkVersion>v4.5</TargetFrameworkVersion>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="System" />
                {systemWeb}
              </ItemGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(dir, "packages.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <packages>
              <package id="EntityFramework" version="6.1.3" targetFramework="net45" />
            </packages>
            """);

        File.WriteAllText(Path.Combine(dir, "Handler.cs"), """
            namespace Legacy { public class Handler { public string Run(string s) { return s.Trim(); } } }
            """);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_source, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
