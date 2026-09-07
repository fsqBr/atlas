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

/// <summary>Interop with other tools: SBOM (CycloneDX), findings export (csv/json/sarif), SARIF import, issue export and the compliance bundle.</summary>
internal static class AssessmentInteropEndpoints
{
    public static void Map(RouteGroupBuilder assessments)
    {
        // Findings as a file: csv | json | sarif (SARIF 2.1.0 for GitHub/Azure DevOps code scanning).

        // SBOM (CycloneDX 1.5) from the components the license scanner recorded on the latest run.
        assessments.MapGet("/{id:guid}/sbom", async (Guid id, IAssessmentRepository repository, IFindingRepository findings, CancellationToken ct) =>
        {
            var assessment = await repository.GetAsync(id, ct);
            if (assessment is null)
            {
                return Results.NotFound();
            }

            var page = await findings.ListAsync(id, 0, 5, ct, new FindingFilter(RuleId: SbomBuilder.InventoryRuleId, Search: null));
            var latest = page.Items.OrderByDescending(i => i.Finding.UpdatedAtUtc).FirstOrDefault()?.Latest;
            string? components = null;
            if (latest?.DataJson is not null)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(latest.DataJson);
                    if (doc.RootElement.TryGetProperty("components", out var c))
                    {
                        components = c.GetString();
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                }
            }

            var bom = SbomBuilder.Build(assessment, components, typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0", DateTimeOffset.UtcNow);
            if (bom is null)
            {
                return Results.Json(new { error = "No license inventory yet: run the assessment (license.compliance scanner) first." }, statusCode: StatusCodes.Status404NotFound);
            }

            var safeName = string.Concat(assessment.Name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')).ToLowerInvariant();
            return Results.File(System.Text.Encoding.UTF8.GetBytes(bom), "application/vnd.cyclonedx+json", $"{safeName}-sbom.cdx.json");
        });

        assessments.MapGet("/{id:guid}/findings/export", async (
            Guid id,
            IAssessmentRepository repository,
            IFindingRepository findingRepository,
            IRuleCatalog ruleCatalog,
            CancellationToken ct,
            string format = "csv",
            string? lang = null,
            string? status = null) =>
        {
            var assessment = await repository.GetAsync(id, ct);
            if (assessment is null)
            {
                return Results.NotFound();
            }

            FindingStatus? statusFilter = Enum.TryParse<FindingStatus>(status, true, out var parsed) ? parsed : null;
            var page = await findingRepository.ListAsync(id, 0, 50_000, ct, new FindingFilter(Status: statusFilter));
            var rules = await ruleCatalog.GetAllAsync(ct);
            var safeName = string.Concat(assessment.Name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ')).Trim().Replace(' ', '-');
            var stem = $"atlas-findings-{(safeName.Length == 0 ? id.ToString("N")[..8] : safeName)}";

            return format.ToLowerInvariant() switch
            {
                "csv" => Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(FindingExporter.ToCsv(page.Items, rules, lang))).ToArray(), "text/csv; charset=utf-8", stem + ".csv"),
                "json" => Results.File(System.Text.Encoding.UTF8.GetBytes(FindingExporter.ToJson(page.Items, rules, lang)), "application/json", stem + ".json"),
                "sarif" => Results.File(System.Text.Encoding.UTF8.GetBytes(FindingExporter.ToSarif(page.Items, rules, typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0", lang)), "application/sarif+json", stem + ".sarif"),
                _ => Results.BadRequest(new { error = "format must be csv, json or sarif." }),
            };
        });

        // Import a SARIF 2.1.0 log from an external tool (ESLint, Semgrep, Trivy…): each tool becomes its
        // own scanner ("external.{tool}"), so a later import from the same tool resolves what it no longer
        // reports, and Atlas's own scans never touch these findings.
        assessments.MapPost("/{id:guid}/sarif", async (
            Guid id,
            HttpRequest httpRequest,
            IAssessmentRepository repository,
            IAssessmentRunRepository runsRepo,
            IScanRepository scansRepo,
            IRuleCatalog ruleCatalog,
            IFindingRepository findingsRepo,
            IInventoryRepository inventoryRepo,
            IHealthRepository healthRepo,
            ISuppressionPolicyRepository policiesRepo,
            IRuleOverrideRepository ruleOverrides,
            IUnitOfWork unitOfWork,
            ILogger<Program> importLogger,
            CancellationToken ct) =>
        {
            var assessment = await repository.GetAsync(id, ct);
            if (assessment is null)
            {
                return Results.NotFound();
            }

            // The limit is enforced while reading, not only via Content-Length: chunked encoding carries
            // no length and would otherwise buffer up to Kestrel's global (1 GB) cap into one string.
            const long maxSarifBytes = 25_000_000;
            if (httpRequest.ContentLength is > maxSarifBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            string json;
            using (var buffered = new MemoryStream())
            {
                var chunk = new byte[81_920];
                long total = 0;
                int readBytes;
                while ((readBytes = await httpRequest.Body.ReadAsync(chunk, ct)) > 0)
                {
                    total += readBytes;
                    if (total > maxSarifBytes)
                    {
                        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                    }

                    buffered.Write(chunk, 0, readBytes);
                }

                json = System.Text.Encoding.UTF8.GetString(buffered.GetBuffer(), 0, (int)buffered.Length);
            }

            Atlas.Application.Findings.SarifImport import;
            try
            {
                import = Atlas.Application.Findings.SarifImporter.Parse(json);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = "Not a readable SARIF 2.1.0 log: " + ex.Message });
            }

            var run = new Atlas.Domain.Assessments.AssessmentRun(Guid.NewGuid(), assessment.TenantId, id, await runsRepo.NextNumberAsync(id, ct));
            runsRepo.Add(run);
            var scan = Atlas.Domain.Scans.Scan.Start(Guid.NewGuid(), assessment.TenantId, id, Guid.Empty, import.ScannerId, import.ToolVersion, null, run.Id);
            scansRepo.Add(scan);

            try
            {
                var rules = await ruleCatalog.UpsertAsync(import.ScannerId, import.Rules, ct);
                var owned = (await ruleCatalog.ListRuleIdsByScannerAsync(import.ScannerId, ct)).Union(rules.Keys, StringComparer.Ordinal).ToList();
                var existing = await findingsRepo.GetByAssessmentAndRulesAsync(id, owned, ct);

                // Same pipeline as the scan runner: standing suppression policies drop candidates, and the
                // tenant's severity tuning applies — the catalog dropdown works for imported rules too.
                var candidates = import.Candidates;
                var activePolicies = await policiesRepo.ListForAssessmentAsync(id, ct);
                if (activePolicies.Count > 0)
                {
                    candidates = Atlas.Application.Findings.SuppressionPolicyHandler.Filter(candidates, activePolicies, out _);
                }

                var severityOverrides = await ruleOverrides.MapForTenantAsync(assessment.TenantId, ct);
                if (severityOverrides.Count > 0)
                {
                    candidates = candidates
                        .Select(c => severityOverrides.TryGetValue(c.RuleId, out var tuned) && tuned != c.Severity ? c with { Severity = tuned } : c)
                        .ToList();
                }

                var reconciliation = Atlas.Application.Findings.FindingReconciler.Reconcile(
                    assessment.TenantId, id, scan.Id, import.ScannerId, import.ToolVersion,
                    assessment.RepositoryKey, candidates, rules, existing, scanSucceeded: true);

                findingsRepo.AddRange(reconciliation.Created);
                findingsRepo.AddOccurrences(reconciliation.Occurrences);
                scan.Succeed(candidates.Count, reconciliation.Created.Count, reconciliation.Recurring, reconciliation.Resolved, reconciliation.Regressed);
                run.RecordScan(succeeded: true, reconciliation.Created.Count, reconciliation.Recurring, reconciliation.Resolved, reconciliation.Regressed);
                await unitOfWork.SaveChangesAsync(ct);

                var open = await findingsRepo.ListOpenAsync(id, ct);
                var projectCount = (await inventoryRepo.GetLatestByAssessmentAsync(id, ct)).Sum(i => i.ProjectCount);
                var snapshot = HealthSnapshotFactory.Create(assessment.TenantId, id, null, open, projectCount, run.Id);
                healthRepo.Add(snapshot);
                run.Complete(open.Count, snapshot.Score);
                await unitOfWork.SaveChangesAsync(ct);

                return Results.Ok(new
                {
                    runId = run.Id,
                    runNumber = run.Number,
                    tool = import.ToolName,
                    scannerId = import.ScannerId,
                    imported = import.Candidates.Count,
                    newFindings = reconciliation.Created.Count,
                    recurring = reconciliation.Recurring,
                    resolved = reconciliation.Resolved,
                    runsIgnored = import.RunsIgnored,
                    healthScore = snapshot.Score,
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Never leave a Running run behind (the orphan-run failure mode): mark run and scan failed
                // with a save that cannot be cancelled, then surface the error.
                importLogger.LogError(ex, "SARIF import failed for assessment {AssessmentId}.", id);
                TryCompensate(run, scan, ex);
                try
                {
                    await unitOfWork.SaveChangesAsync(CancellationToken.None);
                }
                catch (Exception saveEx)
                {
                    importLogger.LogError(saveEx, "SARIF import compensation failed for assessment {AssessmentId}.", id);
                }

                return Results.Problem(title: "SARIF import failed", detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
            catch (OperationCanceledException)
            {
                TryCompensate(run, scan, null);
                try
                {
                    await unitOfWork.SaveChangesAsync(CancellationToken.None);
                }
                catch
                {
                    // best effort: the client is gone
                }

                throw;
            }

            static void TryCompensate(Atlas.Domain.Assessments.AssessmentRun run, Atlas.Domain.Scans.Scan scan, Exception? ex)
            {
                var reason = ex is null ? "SARIF import cancelled." : "SARIF import failed: " + (ex.Message.Length > 900 ? ex.Message[..900] : ex.Message);
                if (scan.Status == Atlas.Domain.Scans.ScanStatus.Running)
                {
                    scan.Fail(reason);
                }

                if (run.Status == Atlas.Domain.Assessments.AssessmentRunStatus.Running)
                {
                    run.Fail(reason);
                }
            }
        });

        // Turn the top open findings into issues/work items on the assessed repository's own tracker.
        assessments.MapPost("/{id:guid}/export/issues", async (
            Guid id, IssueExportRequest request, IAssessmentRepository repository, IFindingRepository findingsRepo,
            IRuleCatalog ruleCatalog, IssueExportService exporter, IConfiguration configuration, IUnitOfWork exportUnitOfWork, CancellationToken ct, string? lang = null) =>
        {
            var assessment = await repository.GetAsync(id, ct);
            if (assessment is null)
            {
                return Results.NotFound();
            }

            var top = Math.Clamp(request.Top ?? 10, 1, IssueExportService.MaxIssues);
            var page = await findingsRepo.ListAsync(id, 0, top, ct, new FindingFilter(Status: FindingStatus.Open));
            if (page.Items.Count == 0)
            {
                return Results.BadRequest(new { error = "No open findings to export." });
            }

            try
            {
                var result = await exporter.ExportAsync(
                    assessment, page.Items, await ruleCatalog.GetAllAsync(ct),
                    configuration["Atlas:Notifications:PublicBaseUrl"] ?? "", lang, ct);
                await exportUnitOfWork.SaveChangesAsync(ct); // persists the credential's last-used audit stamp
                return Results.Ok(new IssueExportResponse(result.Created, result.Urls, result.Errors));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Everything a compliance review asks for, in one download: privacy findings, license inventory,
        // SBOM, and every waiver with reason, author and expiry.
        assessments.MapGet("/{id:guid}/compliance.zip", async (
            Guid id, IAssessmentRepository repository, IFindingRepository findingsRepo, IRuleCatalog ruleCatalog,
            ISuppressionRepository suppressionsRepo, ISuppressionPolicyRepository policiesRepo, CancellationToken ct, string? lang = null) =>
        {
            var assessment = await repository.GetAsync(id, ct);
            if (assessment is null)
            {
                return Results.NotFound();
            }

            var rules = await ruleCatalog.GetAllAsync(ct);
            var all = await findingsRepo.ListAsync(id, 0, 50_000, ct);
            var privacy = all.Items.Where(i => i.Finding.Category == FindingCategory.Data).ToList();
            var licenses = all.Items.Where(i => i.Finding.RuleId.StartsWith("license.", StringComparison.Ordinal)).ToList();
            var suppressionRows = await suppressionsRepo.ListByAssessmentAsync(id, ct);
            var policyRows = await policiesRepo.ListForAssessmentAsync(id, ct);

            string Csv(string value)
            {
                // Same guard as FindingExporter: neutralize spreadsheet formula injection from free text.
                if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
                {
                    value = "'" + value;
                }

                return '"' + value.Replace("\"", "\"\"") + '"';
            }
            var waivers = new System.Text.StringBuilder("kind,scope,reason,author,createdAtUtc,expiresAtUtc,revokedAtUtc\n");
            foreach (var w in suppressionRows)
            {
                waivers.Append($"{w.Kind},{Csv(w.Fingerprint)},{Csv(w.Reason)},{Csv(w.Author)},{w.CreatedAtUtc:O},{w.ExpiresAtUtc:O},{w.RevokedAtUtc:O}\n");
            }

            foreach (var p in policyRows)
            {
                waivers.Append($"Policy,{Csv(p.RulePattern + (p.PathGlob is null ? "" : " @ " + p.PathGlob))},{Csv(p.Reason)},{Csv(p.Author)},{p.CreatedAtUtc:O},{p.ExpiresAtUtc:O},\n");
            }

            var summary = $"# Atlas compliance pack — {assessment.Name}\n\nGenerated {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC by Atlas {typeof(Program).Assembly.GetName().Version?.ToString(3)}.\n\n" +
                $"- Privacy (personal data) findings: {privacy.Count}\n- License findings: {licenses.Count}\n- Waivers and policies: {suppressionRows.Count + policyRows.Count}\n\n" +
                "Contents: privacy-findings.csv, license-findings.csv, waivers.csv" + "\n\nClassifications are aids to a human review, not legal advice.\n";

            using var buffer = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                async Task AddAsync(string name, string content)
                {
                    var entry = zip.CreateEntry(name);
                    await using var stream = entry.Open();
                    if (name.EndsWith(".csv", StringComparison.Ordinal))
                    {
                        var preamble = System.Text.Encoding.UTF8.GetPreamble();
                        await stream.WriteAsync(preamble, ct); // BOM: Excel double-click opens pt-BR text correctly
                    }

                    var bytes = System.Text.Encoding.UTF8.GetBytes(content);
                    await stream.WriteAsync(bytes, ct);
                }

                await AddAsync("summary.md", summary);
                await AddAsync("privacy-findings.csv", Atlas.Application.Findings.FindingExporter.ToCsv(privacy, rules, lang));
                await AddAsync("license-findings.csv", Atlas.Application.Findings.FindingExporter.ToCsv(licenses, rules, lang));
                await AddAsync("waivers.csv", waivers.ToString());
            }

            var safeName = string.Concat(assessment.Name.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')).ToLowerInvariant();
            return Results.File(buffer.ToArray(), "application/zip", $"atlas-compliance-{(safeName.Length == 0 ? id.ToString("N")[..8] : safeName)}.zip");
        });
    }
}
