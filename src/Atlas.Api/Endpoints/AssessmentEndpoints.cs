using Atlas.Api;
using Atlas.Api.Endpoints;
using Atlas.Application;
using Atlas.Application.Assessments;
using Atlas.Application.Credentials;
using Atlas.Application.Findings;
using Atlas.Connector.Abstractions;
using Atlas.Connector.AzureDevOps;
using Atlas.Connector.Git;
using Atlas.Connector.GitHub;
using Atlas.Connector.GitLab;
using Atlas.Ai;
using Atlas.Application.Ai;
using Atlas.Application.Security;
using Atlas.Application.Tenants;
using Atlas.Connector.Upload;
using Atlas.Language.Abstractions;
using Atlas.Language.CSharp;
using Atlas.Language.Sql;
using Atlas.Language.VisualBasic;
using Atlas.Connector.Local;
using Atlas.Contracts.Assessments;
using Atlas.Domain.Assessments;
using Atlas.Domain.Findings;
using Atlas.Domain.Sources;
using Atlas.Domain.Tenants;
using Atlas.Governance.Infrastructure;
using Atlas.Infrastructure;
using Atlas.Infrastructure.Persistence;
using Atlas.Reporting;
using Atlas.Scanner.Ai;
using Atlas.Scanner.Architecture;
using Atlas.Scanner.Database;
using Atlas.Scanner.JavaScript;
using Atlas.Scanner.Licenses;
using Atlas.Scanner.Dependencies;
using Atlas.Scanner.Infrastructure;
using Atlas.Scanner.Privacy;
using Atlas.Scanner.Quality;
using Atlas.Scanner.Runtime;
using Atlas.Scanner.Secrets;
using Atlas.Scanner.Security;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Api.Endpoints;

/// <summary>Assessment lifecycle: list/create/get/patch/delete, scope and tags, quality gate and PR comment, findings and triage, modernization, actuals, schedule, runs, health, reports, sharing and re-upload.</summary>
internal static class AssessmentEndpoints
{
    public static void Map(RouteGroupBuilder assessments)
    {
        assessments.MapGet("/", async (
            IAssessmentRepository repository, IHealthRepository health, IScanJobQueue jobs, CancellationToken ct) =>
        {
            var list = await repository.ListRecentAsync(100, ct);
            var ids = list.Select(a => a.Id).ToList();
            var scores = await health.GetLatestForAsync(ids, ct);
            var activeJobs = await jobs.GetActiveJobStatesAsync(ids, ct);
            return Results.Ok(list.Select(a => ApiMapping.ToSummary(
                a, scores.GetValueOrDefault(a.Id), activeJobs.TryGetValue(a.Id, out var s) ? s : null)));
        });

        assessments.MapPost("/", async (
            CreateAssessmentRequest request,
            CreateAssessmentHandler handler,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.SourceKind)
                || string.IsNullOrWhiteSpace(request.SourceLocator))
            {
                return Results.BadRequest(new { error = "name, sourceKind and sourceLocator are required." });
            }

            try
            {
                var result = await handler.HandleAsync(
                    request.Name,
                    new SourceReference(request.SourceKind, request.SourceLocator, request.Branch,
                        string.IsNullOrWhiteSpace(request.CredentialName) ? null : request.CredentialName.Trim()),
                    ct,
                    request.ExcludePaths);

                return Results.Accepted(
                    $"/api/assessments/{result.AssessmentId}",
                    new AssessmentCreatedResponse(result.AssessmentId, result.JobId));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        assessments.MapGet("/{id:guid}", async (
            Guid id,
            IAssessmentRepository repository,
            IScanRepository scans,
            IScanJobQueue jobs,
            CancellationToken ct) =>
        {
            var assessment = await repository.GetAsync(id, ct);
            if (assessment is null)
            {
                return Results.NotFound();
            }

            var scanList = await scans.ListByAssessmentAsync(id, ct);
            var activeJobs = await jobs.GetActiveJobStatesAsync([id], ct);
            return Results.Ok(ApiMapping.ToResponse(assessment, scanList, activeJobs.TryGetValue(id, out var s) ? s : null));
        });

        assessments.MapPatch("/{id:guid}", async (
            Guid id, RenameAssessmentRequest request, IAssessmentRepository repository, IUnitOfWork unitOfWork, CancellationToken ct) =>
        {
            var assessment = await repository.GetAsync(id, ct);
            if (assessment is null)
            {
                return Results.NotFound();
            }

            try
            {
                assessment.Rename(request.Name);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            await unitOfWork.SaveChangesAsync(ct);
            return Results.Ok(new { id = assessment.Id, name = assessment.Name });
        });

        // Analysis scope: gitignore-like globs excluded on the next run (on top of defaults and the repo's .atlasignore).
        assessments.MapPut("/{id:guid}/scope", async (Guid id, ScopeRequest request, IAssessmentRepository repository, IUnitOfWork unitOfWork, CancellationToken ct) =>
        {
            var assessment = await repository.GetAsync(id, ct);
            if (assessment is null)
            {
                return Results.NotFound();
            }

            assessment.SetExcludeGlobs(request.ExcludePaths);
            await unitOfWork.SaveChangesAsync(ct);
            return Results.Ok(new { id, excludePaths = assessment.ExcludeGlobs, defaults = Atlas.Domain.Workspaces.PathExclusions.DefaultGlobs });
        });

        assessments.MapDelete("/{id:guid}", async (Guid id, DeleteAssessmentHandler handler, IAssessmentRepository repository, UploadOptions uploads, CancellationToken ct) =>
        {
            try
            {
                var existing = await repository.GetAsync(id, ct);
                if (existing is null || !await handler.HandleAsync(id, ct))
                {
                    return Results.NotFound();
                }

                if (existing.SourceKind == SourceReference.Kinds.Upload)
                {
                    UploadGcService.DeleteUpload(uploads.Directory, existing.SourceLocator);
                }

                return Results.NoContent();
            }
            catch (AssessmentBusyException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        // ---- CI integration: find the assessment of a repository and evaluate the quality gate ----

        // Side-by-side: two assessments, same columns (both must be visible to the caller).
        assessments.MapGet("/compare", async (Guid a, Guid b, Atlas.Application.Portfolio.SideBySideComparisonBuilder builder, CancellationToken ct, string? lang = null) =>
        {
            if (a == b)
            {
                return Results.BadRequest(new { error = "Pick two different assessments." });
            }

            var comparison = await builder.BuildAsync(a, b, lang, ct);
            return comparison is null ? Results.NotFound() : Results.Ok(ApiMapping.ToResponse(comparison));
        });

        assessments.MapGet("/by-locator", async (IAssessmentRepository repository, IScanJobQueue jobs, CancellationToken ct, string locator, string kind = "git", string? branch = null) =>
        {
            if (string.IsNullOrWhiteSpace(locator))
            {
                return Results.BadRequest(new { error = "locator is required." });
            }

            var assessment = await repository.FindByLocatorAsync(kind.Trim().ToLowerInvariant(), locator.Trim(), branch, ct);
            if (assessment is null)
            {
                return Results.NotFound();
            }

            var active = await jobs.GetActiveJobStatesAsync([assessment.Id], ct);
            return Results.Ok(ApiMapping.ToResponse(assessment, [], active.TryGetValue(assessment.Id, out var st) ? st : null));
        });

        assessments.MapGet("/{id:guid}/gate", async (Guid id, IAssessmentRepository repository, IHealthRepository health, IFindingRepository findings, IAssessmentRunRepository runs, RunComparisonBuilder comparisons, CancellationToken ct, string? failOn = null, int? minScore = null, string? failOnNew = null) =>
        {
            var assessment = await repository.GetAsync(id, ct);
            if (assessment is null)
            {
                return Results.NotFound();
            }

            var snapshot = await health.GetLatestAsync(id, ct);
            var open = await findings.SummarizeOpenAsync([id], ct);
            var bySeverity = Enum.GetValues<Severity>().ToDictionary(s => s, s => open.Where(o => o.Severity == s).Sum(o => o.Count));
            try
            {
                // Baseline mode (?failOnNew=severity): only findings the LATEST run introduced count.
                // The first completed run establishes the baseline — nothing is "new" on it.
                IReadOnlyDictionary<Severity, int>? newBySeverity = null;
                if (!string.IsNullOrWhiteSpace(failOnNew))
                {
                    var latest = (await runs.ListByAssessmentAsync(id, ct))
                        .Where(r => r.Status is Atlas.Domain.Assessments.AssessmentRunStatus.Completed or Atlas.Domain.Assessments.AssessmentRunStatus.CompletedWithWarnings)
                        .OrderByDescending(r => r.Number)
                        .FirstOrDefault();
                    var comparison = latest is null ? null : await comparisons.BuildAsync(id, latest.Id, null, null, ct);
                    // Baseline mode fails on what the latest run INTRODUCED or REINTRODUCED: a Critical that
                    // was fixed and came back is a regression the gate must catch, not a silent pass.
                    newBySeverity = comparison is { Previous: not null }
                        ? MergeSeverities(comparison.NewBySeverity, comparison.RegressedBySeverity)
                        : new Dictionary<Severity, int>();
                }

                var result = QualityGate.Evaluate(snapshot?.Score, bySeverity, failOn, minScore, assessment.CompletedAtUtc is not null, failOnNew, newBySeverity);
                return Results.Ok(new QualityGateResponse(result.Passed, result.Evaluated, result.Score,
                    result.OpenBySeverity.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value), result.Violations, result.FailOn, result.MinScore,
                    $"/api/assessments/{id}/report", result.FailOnNew,
                    result.NewBySeverity?.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Pull-request comment (Markdown) for CI: gate verdict, health and its delta, what the latest run changed, the new findings
        // to look at, the gate's reasons and links. ?ai=true adds a short AI paragraph when a provider is configured (skipped silently otherwise).
        assessments.MapGet("/{id:guid}/pr-comment", async (
            Guid id, IAssessmentRepository repository, IAssessmentRunRepository runs, RunComparisonBuilder comparisons, IHealthRepository health,
            IFindingRepository findings, AiNarrativeService narratives, IConfiguration configuration, CancellationToken ct,
            string? lang = null, string? failOn = null, int? minScore = null, bool ai = false, string? failOnNew = null) =>
        {
            var assessment = await repository.GetAsync(id, ct);
            if (assessment is null)
            {
                return Results.NotFound();
            }

            var snapshot = await health.GetLatestAsync(id, ct);
            var open = await findings.SummarizeOpenAsync([id], ct);
            var bySeverity = Enum.GetValues<Severity>().ToDictionary(s => s, s => open.Where(o => o.Severity == s).Sum(o => o.Count));

            var latest = (await runs.ListByAssessmentAsync(id, ct))
                .Where(r => r.Status is Atlas.Domain.Assessments.AssessmentRunStatus.Completed or Atlas.Domain.Assessments.AssessmentRunStatus.CompletedWithWarnings)
                .OrderByDescending(r => r.Number)
                .FirstOrDefault();
            var comparison = latest is null ? null : await comparisons.BuildAsync(id, latest.Id, null, lang, ct);

            QualityGateResult gate;
            try
            {
                var newBySeverity = string.IsNullOrWhiteSpace(failOnNew)
                    ? null
                    : comparison is { Previous: not null }
                        ? MergeSeverities(comparison.NewBySeverity, comparison.RegressedBySeverity)
                        : new Dictionary<Severity, int>();
                gate = QualityGate.Evaluate(snapshot?.Score, bySeverity, failOn, minScore, assessment.CompletedAtUtc is not null, failOnNew, newBySeverity);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            string? aiText = null;
            string? aiModel = null;
            if (ai && comparison is not null)
            {
                try
                {
                    var summary = await narratives.SummarizeRunAsync(id, comparison, gate, lang, ct);
                    aiText = summary.Text;
                    aiModel = summary.Model;
                }
                catch (Exception ex) when (ex is AiNotConfiguredException or ChatProviderException or HttpRequestException or TaskCanceledException)
                {
                    // the comment stands on the deterministic facts; the AI paragraph is a bonus
                }
            }

            var version = "v" + (typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
            var markdown = PrComment.Render(new PrCommentInput(assessment.Name, id, comparison, gate, configuration["Atlas:Notifications:PublicBaseUrl"], version, lang, aiText, aiModel));
            return Results.Text(markdown, "text/markdown; charset=utf-8");
        });

        assessments.MapGet("/{id:guid}/findings", async (
            Guid id,
            IAssessmentRepository repository,
            IFindingRepository findings,
            IRuleCatalog ruleCatalog,
            ISuppressionRepository suppressions,
            CancellationToken ct,
            int page = 1,
            int pageSize = 50,
            string? severity = null,
            string? category = null,
            string? status = null,
            string? ruleId = null,
            string? search = null,
            string? lang = null) =>
        {
            if (await repository.GetAsync(id, ct) is null)
            {
                return Results.NotFound();
            }

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 200);

            var filter = new FindingFilter(
                Enum.TryParse<Severity>(severity, true, out var sev) ? sev : null,
                Enum.TryParse<FindingCategory>(category, true, out var cat) ? cat : null,
                Enum.TryParse<FindingStatus>(status, true, out var st) ? st : null,
                ruleId,
                search);

            var result = await findings.ListAsync(id, (page - 1) * pageSize, pageSize, ct, filter);
            var rules = await ruleCatalog.GetAllAsync(ct);
            var active = await suppressions.GetActiveForAsync(result.Items.Select(i => i.Finding.Id).ToList(), ct);
            return Results.Ok(new PagedResponse<FindingResponse>(
                result.Items.Select(i => ApiMapping.ToResponse(i, rules, lang, active.GetValueOrDefault(i.Finding.Id))).ToList(),
                page, pageSize, result.Total));
        });

        // Human triage: suppress (accepted), false positive, or reopen. Auditable; recomputes the health score.
        assessments.MapPost("/{id:guid}/findings/{findingId:guid}/triage", async (
            Guid id,
            Guid findingId,
            TriageRequest request,
            TriageFindingHandler handler,
            ISuppressionRepository suppressions,
            IRuleCatalog ruleCatalog,
            IFindingRepository findings,
            CancellationToken ct,
            string? lang = null) =>
        {
            if (!Enum.TryParse<TriageAction>(request.Action, true, out var action))
            {
                return Results.BadRequest(new { error = "action must be Suppress, FalsePositive or Reopen." });
            }

            if (action != TriageAction.Reopen && string.IsNullOrWhiteSpace(request.Reason))
            {
                return Results.BadRequest(new { error = "reason is required to suppress or mark a false positive." });
            }

            if (request.ExpiresAtUtc is not null && action != TriageAction.Suppress)
            {
                return Results.BadRequest(new { error = "expiresAtUtc only applies to Suppress: false positives and reopens do not expire." });
            }

            try
            {
                var finding = await handler.HandleAsync(id, findingId, action, request.Reason, request.Author, ct, request.ExpiresAtUtc);
                var page = await findings.ListAsync(id, 0, 1, ct, new FindingFilter(RuleId: finding.RuleId, Search: null));
                var item = page.Items.FirstOrDefault(i => i.Finding.Id == finding.Id)
                    ?? new FindingWithLatestOccurrence(finding, null);
                var rules = await ruleCatalog.GetAllAsync(ct);
                return Results.Ok(ApiMapping.ToResponse(item, rules, lang, await suppressions.GetActiveAsync(finding.Id, ct)));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Modernization strategy comparison, cost ranges and roadmap — recomputed from persisted facts (deterministic).
        assessments.MapGet("/{id:guid}/modernization", async (Guid id, ModernizationPlanBuilder planBuilder, CancellationToken ct, string? lang = null) =>
        {
            var plan = await planBuilder.BuildAsync(id, ct);
            return plan is null ? Results.NotFound() : Results.Ok(ApiMapping.ToResponse(plan, lang));
        });

        // Free-form labels for portfolio grouping and filtering.
        assessments.MapPut("/{id:guid}/tags", async (Guid id, TagsRequest request, IAssessmentRepository repository, IUnitOfWork unitOfWork, CancellationToken ct) =>
        {
            var assessment = await repository.GetAsync(id, ct);
            if (assessment is null)
            {
                return Results.NotFound();
            }

            assessment.SetTags(request.Tags);
            await unitOfWork.SaveChangesAsync(ct);
            return Results.Ok(new { id = assessment.Id, tags = assessment.Tags });
        });

        // Real outcomes for cost calibration: one record per assessment, replaced on re-record.
        assessments.MapGet("/{id:guid}/actuals", async (Guid id, IModernizationActualRepository repository, CancellationToken ct, string? lang = null) =>
        {
            var actual = await repository.GetAsync(id, ct);
            return actual is null ? Results.NotFound() : Results.Ok(ApiMapping.ToResponse(actual, lang));
        });

        assessments.MapPut("/{id:guid}/actuals", async (
            Guid id, RecordActualRequest request, IAssessmentRepository assessmentsRepo, IModernizationActualRepository repository, ModernizationPlanBuilder planBuilder, IUnitOfWork unitOfWork, CancellationToken ct, string? lang = null) =>
        {
            var assessment = await assessmentsRepo.GetAsync(id, ct);
            if (assessment is null)
            {
                return Results.NotFound();
            }

            if (!Enum.TryParse<Atlas.Domain.Modernization.ModernizationStrategy>(request.Strategy, true, out var strategy))
            {
                return Results.BadRequest(new { error = "Unknown strategy." });
            }

            try
            {
                // Freeze the estimate now: calibration compares against what was promised, and the plan
                // recomputed after the modernization no longer reflects it.
                var estimatedHours = request.EstimatedHours;
                if (estimatedHours is null)
                {
                    var plan = await planBuilder.BuildAsync(id, ct);
                    estimatedHours = plan?.Estimates.FirstOrDefault(e => e.Strategy == strategy)?.EffortHours.Likely;
                }

                var existing = await repository.GetAsync(id, ct);
                if (existing is null)
                {
                    existing = new Atlas.Domain.Modernization.ModernizationActual(id, assessment.TenantId, strategy, request.ActualHours, request.ActualMonths, request.ActualCost, request.Currency ?? "BRL", request.Notes, request.RecordedBy, estimatedHours);
                    repository.Add(existing);
                }
                else
                {
                    existing.Update(strategy, request.ActualHours, request.ActualMonths, request.ActualCost, request.Currency ?? existing.Currency, request.Notes, request.RecordedBy, estimatedHours);
                }

                await unitOfWork.SaveChangesAsync(ct);
                return Results.Ok(ApiMapping.ToResponse(existing, lang));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Cadence + webhook for continuous re-assessment.
        assessments.MapPut("/{id:guid}/schedule", async (Guid id, ScheduleRequest request, IAssessmentRepository repository, IUnitOfWork unitOfWork, CancellationToken ct) =>
        {
            var assessment = await repository.GetAsync(id, ct);
            if (assessment is null)
            {
                return Results.NotFound();
            }

            try
            {
                assessment.SetSchedule(request.RerunEveryDays, request.WebhookUrl);
                assessment.SetTarget(request.TargetScore, request.TargetDate);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            await unitOfWork.SaveChangesAsync(ct);
            return Results.Ok(new { id, rerunEveryDays = assessment.RerunEveryDays, webhookUrl = assessment.WebhookUrl });
        });

        // Aggregate views over open findings: by rule (what) and by folder (where).
        assessments.MapGet("/{id:guid}/findings/by-rule", async (Guid id, FindingViewsBuilder views, CancellationToken ct, string? lang = null) =>
            Results.Ok((await views.ByRuleAsync(id, lang, ct)).Select(g => new RuleGroupResponse(g.RuleId, g.Title, g.Category.ToString(), g.MaxSeverity.ToString(), g.Count, g.SampleFiles))));

        assessments.MapGet("/{id:guid}/findings/heatmap", async (Guid id, FindingViewsBuilder views, CancellationToken ct, int depth = 2) =>
            Results.Ok((await views.HeatmapAsync(id, Math.Clamp(depth, 1, 6), ct)).Select(r => new HeatmapRowResponse(r.Folder, r.Open, r.Critical, r.High, r.Medium, r.Low, r.Informational, r.Files))));

        assessments.MapGet("/{id:guid}/runs", async (
            Guid id,
            IAssessmentRepository repository,
            IAssessmentRunRepository runs,
            CancellationToken ct) =>
        {
            if (await repository.GetAsync(id, ct) is null)
            {
                return Results.NotFound();
            }

            return Results.Ok((await runs.ListByAssessmentAsync(id, ct)).Select(ApiMapping.ToResponse));
        });

        // ---- Sharing: who can see/edit a restricted assessment (owners + tenant admins manage) ----
        assessments.MapGet("/{id:guid}/access", async (Guid id, IAssessmentRepository repository, AssessmentAccessService access, CancellationToken ct) =>
        {
            if (await repository.GetAsync(id, ct) is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(ApiMapping.ToResponse(await access.GetAsync(id, ct)));
        });

        assessments.MapPut("/{id:guid}/access", async (Guid id, AccessGrantRequest request, AssessmentAccessService access, CancellationToken ct) =>
        {
            if (!Enum.TryParse<AccessRole>(request.Role, ignoreCase: true, out var role))
            {
                return Results.BadRequest(new { error = "role must be Viewer, Editor or Owner." });
            }

            try
            {
                return Results.Ok(ApiMapping.ToResponse(await access.GrantAsync(id, request.Subject, request.SubjectName, role, ct)));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        assessments.MapDelete("/{id:guid}/access/{entryId:guid}", async (Guid id, Guid entryId, AssessmentAccessService access, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(ApiMapping.ToResponse(await access.RevokeAsync(id, entryId, ct)));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        // Re-upload: point an "upload" assessment at a new archive (already posted to /api/uploads) and queue a run.
        assessments.MapPut("/{id:guid}/upload", async (
            Guid id, ReplaceUploadRequest request, IAssessmentRepository repository, IUnitOfWork unitOfWork, RunAgainHandler runAgain, UploadOptions uploads, CancellationToken ct) =>
        {
            var assessment = await repository.GetAsync(id, ct);
            if (assessment is null)
            {
                return Results.NotFound();
            }

            string archive;
            try
            {
                archive = UploadConnector.ArchivePath(uploads, request.UploadId);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            if (!File.Exists(archive))
            {
                return Results.BadRequest(new { error = $"Upload '{request.UploadId}' was not found; post the archive to /api/uploads first." });
            }

            var previous = assessment.SourceLocator;
            try
            {
                assessment.ReplaceUpload(request.UploadId);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            await unitOfWork.SaveChangesAsync(ct);
            if (!string.Equals(previous, assessment.SourceLocator, StringComparison.OrdinalIgnoreCase))
            {
                UploadGcService.DeleteUpload(uploads.Directory, previous);
            }

            try
            {
                var jobId = await runAgain.HandleAsync(id, ct);
                return Results.Accepted($"/api/assessments/{id}", new RunQueuedResponse(jobId));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        assessments.MapPost("/{id:guid}/runs", async (Guid id, RunAgainHandler handler, CancellationToken ct) =>
        {
            try
            {
                var jobId = await handler.HandleAsync(id, ct);
                return Results.Accepted($"/api/assessments/{id}", new RunQueuedResponse(jobId));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        assessments.MapGet("/{id:guid}/runs/{runId:guid}/comparison", async (
            Guid id,
            Guid runId,
            RunComparisonBuilder builder,
            CancellationToken ct,
            Guid? with = null,
            string? lang = null) =>
        {
            var comparison = await builder.BuildAsync(id, runId, with, lang, ct);
            return comparison is null ? Results.NotFound() : Results.Ok(ApiMapping.ToResponse(comparison));
        });

        assessments.MapGet("/{id:guid}/health", async (
            Guid id,
            IHealthRepository healthRepository,
            CancellationToken ct) =>
        {
            var snapshot = await healthRepository.GetLatestAsync(id, ct);
            return snapshot is null ? Results.NotFound() : Results.Ok(ApiMapping.ToResponse(snapshot));
        });

        assessments.MapGet("/{id:guid}/report", async (
            Guid id,
            ExecutiveReportBuilder reportBuilder,
            ReportOptions reportOptions,
            CancellationToken ct,
            string? lang = null,
            DateTimeOffset? since = null) =>
        {
            var locale = ReportLocale.For(lang);
            var report = await reportBuilder.BuildAsync(id, locale, ct, since);
            return report is null
                ? Results.NotFound()
                : Results.Content(HtmlReportRenderer.Render(report, locale, reportOptions), "text/html; charset=utf-8");
        });

        assessments.MapGet("/{id:guid}/report.pdf", async (
            Guid id,
            ExecutiveReportBuilder reportBuilder,
            IPdfRenderer pdf,
            ReportOptions reportOptions,
            ILogger<Program> logger,
            CancellationToken ct,
            string? lang = null,
            DateTimeOffset? since = null) =>
        {
            var locale = ReportLocale.For(lang);
            var report = await reportBuilder.BuildAsync(id, locale, ct, since);
            if (report is null)
            {
                return Results.NotFound();
            }

            try
            {
                var bytes = await pdf.RenderAsync(HtmlReportRenderer.Render(report, locale, reportOptions), ct, HtmlReportRenderer.RenderPdfFooter(report, locale));
                var safeName = string.Concat(report.Header.AssessmentName.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ')).Trim().Replace(' ', '-');
                return Results.File(bytes, "application/pdf", $"atlas-{(safeName.Length == 0 ? id.ToString("N")[..8] : safeName)}-{DateTime.UtcNow:yyyyMMdd}.pdf");
            }
            catch (PdfRendererUnavailableException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                logger.LogWarning(ex, "PDF export failed via {Renderer}.", pdf.Description);
                return Results.Json(new { error = $"PDF export failed ({pdf.Description}): {ex.Message}" }, statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }

    // Baseline gate counts what the latest run introduced AND reintroduced (regressions), per finding severity.
    private static IReadOnlyDictionary<Severity, int> MergeSeverities(IReadOnlyDictionary<Severity, int>? a, IReadOnlyDictionary<Severity, int>? b)
    {
        var merged = new Dictionary<Severity, int>();
        foreach (var kv in a ?? new Dictionary<Severity, int>())
        {
            merged[kv.Key] = merged.GetValueOrDefault(kv.Key) + kv.Value;
        }

        foreach (var kv in b ?? new Dictionary<Severity, int>())
        {
            merged[kv.Key] = merged.GetValueOrDefault(kv.Key) + kv.Value;
        }

        return merged;
    }
}
