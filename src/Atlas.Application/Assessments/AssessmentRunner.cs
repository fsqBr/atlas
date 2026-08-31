using Atlas.Application.Findings;
using Atlas.Application.Workspaces;
using Atlas.Domain.Assessments;
using Atlas.Domain.Scans;
using Atlas.Domain.Workspaces;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;
using Microsoft.Extensions.Logging;

namespace Atlas.Application.Assessments;

/// <summary>
/// Runs one assessment end to end as a numbered run: prepare workspace → execute
/// (language analysis + scanners, in-process or in a disposable child process) →
/// reconcile findings → health score → persist. Every scanner gets its own Scan
/// row; one scanner failing degrades the run to CompletedWithWarnings instead of
/// hiding the gap.
/// </summary>
public sealed class AssessmentRunner(
    IAssessmentRepository assessments,
    IAssessmentRunRepository runs,
    IScanRepository scans,
    IFindingRepository findings,
    IRuleCatalog ruleCatalog,
    IInventoryRepository inventory,
    IHealthRepository health,
    IUnitOfWork unitOfWork,
    IWorkspaceManager workspaces,
    IScanExecutor executor,
    IEnumerable<IScanner> scanners,
    ISuppressionPolicyRepository policies,
    IRuleOverrideRepository ruleOverrides,
    ILogger<AssessmentRunner> logger)
{
    public async Task RunAsync(Guid assessmentId, CancellationToken cancellationToken)
    {
        var assessment = await assessments.GetAsync(assessmentId, cancellationToken)
            ?? throw new InvalidOperationException($"Assessment {assessmentId} not found.");

        assessment.Start();
        var run = new AssessmentRun(
            Guid.NewGuid(), assessment.TenantId, assessment.Id,
            await runs.NextNumberAsync(assessment.Id, cancellationToken));
        runs.Add(run);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        Workspace? workspace = null;
        try
        {
            workspace = await workspaces.PrepareAsync(assessment.Source, cancellationToken);
            run.SetCommit(workspace.CommitSha);

            // One Scan row per scanner that still needs to run for this commit (skip already-succeeded ones).
            var pending = new Dictionary<string, (IScanner Scanner, Scan Scan)>(StringComparer.Ordinal);
            foreach (var scanner in scanners)
            {
                var descriptor = scanner.Descriptor;
                if (workspace.CommitSha is not null
                    && await scans.HasSucceededAsync(assessment.Id, descriptor.Id, workspace.CommitSha, cancellationToken))
                {
                    logger.LogInformation(
                        "Skipping {Scanner}: commit {Commit} already scanned for assessment {AssessmentId}.",
                        descriptor.Id, workspace.CommitSha, assessment.Id);
                    continue;
                }

                var scan = Scan.Start(
                    Guid.NewGuid(), assessment.TenantId, assessment.Id, workspace.Id,
                    descriptor.Id, descriptor.Version, workspace.CommitSha, run.Id);
                scans.Add(scan);
                pending[descriptor.Id] = (scanner, scan);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);

            var request = new WorkspaceScanRequest(
                assessment.Id,
                assessment.RepositoryKey,
                workspace.RootPath,
                pending.ToDictionary(p => p.Key, p => p.Value.Scan.Id, StringComparer.Ordinal),
                DateOnly.FromDateTime(DateTime.UtcNow),
                workspace.History,
                assessment.ExcludeGlobs);

            WorkspaceScanOutcome outcome;
            try
            {
                outcome = await executor.ExecuteAsync(request, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The whole execution failed (e.g. the scan host crashed or timed out): every pending scan failed.
                logger.LogError(ex, "Scan execution failed for assessment {AssessmentId} run #{Number}.", assessment.Id, run.Number);
                outcome = new WorkspaceScanOutcome(
                    new Dictionary<string, LanguageAnalysisResult>(),
                    pending.Keys.Select(id => ScannerOutcome.Failed(id, $"Scan execution failed: {ex.Message}")).ToList());
            }

            foreach (var result in outcome.Languages.Values)
            {
                inventory.Add(InventorySnapshotFactory.FromLanguage(assessment.TenantId, assessment, workspace, result, run.Id));
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);

            // Standing suppression policies drop candidates before reconciliation (coverage-aware → they resolve).
            var activePolicies = await policies.ListForAssessmentAsync(assessment.Id, cancellationToken);
            var severityOverrides = await ruleOverrides.MapForTenantAsync(assessment.TenantId, cancellationToken);

            var anyFailed = false;
            foreach (var (scannerId, (scanner, scan)) in pending)
            {
                var scannerOutcome = outcome.Scanners.FirstOrDefault(o => o.ScannerId == scannerId)
                    ?? ScannerOutcome.Failed(scannerId, "The scan host returned no result for this scanner.");
                if (scannerOutcome.Succeeded && activePolicies.Count > 0)
                {
                    var kept = SuppressionPolicyHandler.Filter(scannerOutcome.Candidates, activePolicies, out var dropped);
                    if (dropped > 0)
                    {
                        logger.LogInformation("Scanner {Scanner}: {Dropped} candidate(s) covered by suppression policies.", scannerId, dropped);
                        scannerOutcome = scannerOutcome with { Candidates = kept };
                    }
                }

                if (scannerOutcome.Succeeded && severityOverrides.Count > 0)
                {
                    // Tenant severity tuning: the fingerprint excludes severity, so history is unaffected.
                    scannerOutcome = scannerOutcome with
                    {
                        Candidates = scannerOutcome.Candidates
                            .Select(c => severityOverrides.TryGetValue(c.RuleId, out var tuned) && tuned != c.Severity ? c with { Severity = tuned } : c)
                            .ToList(),
                    };
                }

                anyFailed |= !await PersistScannerOutcomeAsync(assessment, run, scanner, scan, scannerOutcome, cancellationToken);
            }

            // Health score: deterministic function of the open findings and the estate size (health.v1).
            var openFindings = await findings.ListOpenAsync(assessment.Id, cancellationToken);
            var projectCount = outcome.Languages.Values.Sum(l => l.Projects.Count);
            if (projectCount == 0)
            {
                // The executor crashed or timed out before producing an inventory: falling back to the
                // persisted inventory keeps the size normalization stable instead of collapsing the score.
                projectCount = (await inventory.GetLatestByAssessmentAsync(assessment.Id, cancellationToken)).Sum(i => i.ProjectCount);
            }
            var snapshot = HealthSnapshotFactory.Create(
                assessment.TenantId, assessment.Id, workspace.CommitSha, openFindings, projectCount, run.Id);
            health.Add(snapshot);
            logger.LogInformation(
                "Run #{Number}: health score {Score}/100 ({Level}) from {Open} open finding(s) over {Projects} project(s).",
                run.Number, snapshot.Score, snapshot.RiskLevel, openFindings.Count, projectCount);

            run.Complete(openFindings.Count, snapshot.Score);
            assessment.Complete(withWarnings: anyFailed);
        }
        catch (OperationCanceledException)
        {
            run.Fail("Cancelled.");
            assessment.Fail("Cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Assessment {AssessmentId} run #{Number} failed.", assessment.Id, run.Number);
            run.Fail(ex.Message);
            assessment.Fail(ex.Message);
        }
        finally
        {
            if (workspace is not null)
            {
                await ReleaseQuietlyAsync(workspace.Id);
            }

            await unitOfWork.SaveChangesAsync(CancellationToken.None);
        }
    }

    /// <summary>Reconciles one scanner's candidates into findings. Returns false when the scanner failed.</summary>
    private async Task<bool> PersistScannerOutcomeAsync(
        Assessment assessment,
        AssessmentRun run,
        IScanner scanner,
        Scan scan,
        ScannerOutcome outcome,
        CancellationToken cancellationToken)
    {
        var descriptor = scanner.Descriptor;
        try
        {
            if (!outcome.Succeeded)
            {
                logger.LogWarning("Scanner {Scanner} failed for assessment {AssessmentId}: {Error}", descriptor.Id, assessment.Id, outcome.Error);
                scan.Fail(outcome.Error ?? "Scanner reported failure without details.");
                run.RecordScan(succeeded: false, 0, 0, 0, 0);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return false;
            }

            var rules = await ruleCatalog.UpsertAsync(descriptor.Id, scanner.Rules, cancellationToken);

            // Coverage-aware reconciliation over every rule this scanner has ever owned: findings of a
            // retired rule id are resolved instead of lingering open forever.
            var ownedRuleIds = (await ruleCatalog.ListRuleIdsByScannerAsync(descriptor.Id, cancellationToken))
                .Union(rules.Keys, StringComparer.Ordinal)
                .ToList();
            var existing = await findings.GetByAssessmentAndRulesAsync(assessment.Id, ownedRuleIds, cancellationToken);

            var reconciliation = FindingReconciler.Reconcile(
                assessment.TenantId, assessment.Id, scan.Id, descriptor.Id, descriptor.Version,
                assessment.RepositoryKey, outcome.Candidates, rules, existing, scanSucceeded: true);

            findings.AddRange(reconciliation.Created);
            findings.AddOccurrences(reconciliation.Occurrences);
            scan.Succeed(
                outcome.Candidates.Count, reconciliation.Created.Count, reconciliation.Recurring,
                reconciliation.Resolved, reconciliation.Regressed);
            run.RecordScan(succeeded: true, reconciliation.Created.Count, reconciliation.Recurring,
                reconciliation.Resolved, reconciliation.Regressed);

            logger.LogInformation(
                "Scanner {Scanner}: {Emitted} emitted, {New} new, {Recurring} recurring, {Resolved} resolved, {Regressed} regressed.",
                descriptor.Id, outcome.Candidates.Count, reconciliation.Created.Count,
                reconciliation.Recurring, reconciliation.Resolved, reconciliation.Regressed);

            await unitOfWork.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            scan.Cancel();
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Persisting scanner {Scanner} results failed for assessment {AssessmentId}.", descriptor.Id, assessment.Id);
            scan.Fail(ex.Message);
            run.RecordScan(succeeded: false, 0, 0, 0, 0);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            return false;
        }
    }

    private async Task ReleaseQuietlyAsync(Guid workspaceId)
    {
        try
        {
            await workspaces.ReleaseAsync(workspaceId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to release workspace {WorkspaceId}; GC will collect it.", workspaceId);
        }
    }
}
