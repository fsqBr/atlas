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

/// <summary>AI narratives per assessment: finding explanations and fixes, executive summary, migration plan, feedback and business rules.</summary>
internal static class AiNarrativeEndpoints
{
    public static void Map(RouteGroupBuilder assessments)
    {
        // Cached explanation, if one exists (204 otherwise) — the UI shows it when a finding is expanded without spending tokens.
        assessments.MapGet("/{id:guid}/findings/{findingId:guid}/explain", async (Guid id, Guid findingId, IFindingRepository findings, IAiNarrativeRepository narratives, CancellationToken ct, string? lang = null) =>
        {
            var finding = await findings.GetAsync(findingId, ct);
            if (finding is null || finding.AssessmentId != id)
            {
                return Results.NotFound();
            }

            var n = await narratives.GetAsync(id, Atlas.Domain.Ai.AiNarrative.Kinds.FindingExplanation, finding.Fingerprint, Atlas.Domain.Ai.AiNarrative.NormalizeLang(lang), ct);
            return n is null ? Results.NoContent() : Results.Ok(new NarrativeResponse(n.Text, n.Model, true, n.CreatedAtUtc, n.Rating, n.FeedbackComment));
        });

        // Explain one finding with the model (cached per fingerprint + language). No source code is sent.
        assessments.MapPost("/{id:guid}/findings/{findingId:guid}/explain", async (Guid id, Guid findingId, AiNarrativeService service, CancellationToken ct, string? lang = null, bool refresh = false) =>
        {
            try
            {
                var r = await service.ExplainFindingAsync(id, findingId, lang, refresh, ct);
                return Results.Ok(new NarrativeResponse(r.Text, r.Model, r.Cached, r.CreatedAtUtc, r.Rating, r.FeedbackComment));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (AiNotConfiguredException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status412PreconditionFailed);
            }
            catch (Exception ex) when (ex is ChatProviderException or HttpRequestException or TaskCanceledException)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Fix suggestion: a worker job sends the ~50 lines around the finding (credentials masked; never secrets findings) and stores
        // a diagnosis plus a unified diff, cached per fingerprint and language. POST queues, GET returns the suggestion and job state.
        assessments.MapPost("/{id:guid}/findings/{findingId:guid}/fix", async (Guid id, Guid findingId, QueueFindingFixHandler handler, CancellationToken ct, string? lang = null) =>
        {
            try
            {
                var jobId = await handler.HandleAsync(id, findingId, lang, ct);
                return Results.Accepted($"/api/assessments/{id}/findings/{findingId}/fix", new RunQueuedResponse(jobId));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (AiNotConfiguredException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status412PreconditionFailed);
            }
            catch (FixNotEligibleException ex)
            {
                return Results.UnprocessableEntity(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        assessments.MapGet("/{id:guid}/findings/{findingId:guid}/fix", async (
            Guid id, Guid findingId, IFindingRepository findings, IAiNarrativeRepository narratives, IScanJobQueue queue, CancellationToken ct, string? lang = null) =>
        {
            var finding = await findings.GetAsync(findingId, ct);
            if (finding is null || finding.AssessmentId != id)
            {
                return Results.NotFound();
            }

            var language = Atlas.Domain.Ai.AiNarrative.NormalizeLang(lang);
            var narrative = await narratives.GetAsync(id, Atlas.Domain.Ai.AiNarrative.Kinds.FindingFix, finding.Fingerprint, language, ct);
            var key = findingId.ToString();
            var job = (await queue.ListRecentAsync(200, null, ct))
                .Where(j => j.AssessmentId == id && j.Kind == Atlas.Domain.Jobs.ScanJob.Kinds.FindingFix && j.Payload is not null && j.Payload.Contains(key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(j => j.QueuedAtUtc)
                .FirstOrDefault();
            return Results.Ok(new FindingFixResponse(
                narrative is null ? null : new NarrativeResponse(narrative.Text, narrative.Model, true, narrative.CreatedAtUtc, narrative.Rating, narrative.FeedbackComment),
                job?.State.ToString(), job?.Error));
        });

        // Executive summary written by the model from the report's own figures; appears on page one once generated.
        assessments.MapGet("/{id:guid}/summary", async (Guid id, AiNarrativeService service, CancellationToken ct, string? lang = null) =>
        {
            var r = await service.GetSummaryAsync(id, lang, ct);
            return r is null ? Results.NoContent() : Results.Ok(new NarrativeResponse(r.Text, r.Model, r.Cached, r.CreatedAtUtc, r.Rating, r.FeedbackComment));
        });

        assessments.MapPost("/{id:guid}/summary/generate", async (Guid id, ReportNarrativeService service, CancellationToken ct, string? lang = null) =>
        {
            try
            {
                var r = await service.GenerateSummaryAsync(id, lang, ct);
                return Results.Ok(new NarrativeResponse(r.Text, r.Model, r.Cached, r.CreatedAtUtc, r.Rating, r.FeedbackComment));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (AiNotConfiguredException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status412PreconditionFailed);
            }
            catch (Exception ex) when (ex is ChatProviderException or HttpRequestException or TaskCanceledException)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Migration plan drafted by the model from the modernization plan's own facts (profile, strategy rationale,
        // estimate, roadmap). Markdown, cached per language; the report renders it after the strategy section.
        assessments.MapGet("/{id:guid}/migration-plan", async (Guid id, AiNarrativeService service, CancellationToken ct, string? lang = null) =>
        {
            var r = await service.GetMigrationPlanAsync(id, lang, ct);
            return r is null ? Results.NoContent() : Results.Ok(new NarrativeResponse(r.Text, r.Model, r.Cached, r.CreatedAtUtc, r.Rating, r.FeedbackComment));
        });

        assessments.MapPost("/{id:guid}/migration-plan/generate", async (Guid id, ReportNarrativeService service, CancellationToken ct, string? lang = null) =>
        {
            try
            {
                var r = await service.GenerateMigrationPlanAsync(id, lang, ct);
                return Results.Ok(new NarrativeResponse(r.Text, r.Model, r.Cached, r.CreatedAtUtc, r.Rating, r.FeedbackComment));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (AiNotConfiguredException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status412PreconditionFailed);
            }
            catch (Exception ex) when (ex is ChatProviderException or HttpRequestException or TaskCanceledException)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // The plan as a Markdown file (title, AI label and the text as written), for wikis and pull requests.
        assessments.MapGet("/{id:guid}/migration-plan/export", async (Guid id, IAssessmentRepository repository, AiNarrativeService service, CancellationToken ct, string? lang = null) =>
        {
            var assessment = await repository.GetAsync(id, ct);
            if (assessment is null)
            {
                return Results.NotFound();
            }

            var r = await service.GetMigrationPlanAsync(id, lang, ct);
            if (r is null)
            {
                return Results.Json(new { error = "No migration plan yet: generate it on the Modernization tab first." }, statusCode: StatusCodes.Status404NotFound);
            }

            var pt = Atlas.Domain.Ai.AiNarrative.NormalizeLang(lang) == "pt-BR";
            var label = pt
                ? $"Escrito por IA ({r.Model}) a partir dos números do assessment em {r.CreatedAtUtc:yyyy-MM-dd}; revise antes de circular."
                : $"Written by AI ({r.Model}) from the assessment's figures on {r.CreatedAtUtc:yyyy-MM-dd}; review before circulating.";
            var markdown = $"# {assessment.Name} — {(pt ? "Plano de migração (rascunho por IA)" : "Migration plan (AI draft)")}\n\n_{label}_\n\n{r.Text.Trim()}\n";
            var safeName = string.Concat(assessment.Name.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')).ToLowerInvariant();
            return Results.File(System.Text.Encoding.UTF8.GetBytes(markdown), "text/markdown; charset=utf-8", $"{safeName}-migration-plan.md");
        });

        // Thumbs up / down on what the model wrote: finding explanation or fix (findingId), executive summary or migration plan.
        assessments.MapPut("/{id:guid}/ai/feedback", async (Guid id, FeedbackRequest body, AiFeedbackService service, CancellationToken ct, string kind = "", Guid? findingId = null, string? lang = null) =>
        {
            try
            {
                var n = await service.RateNarrativeAsync(id, kind, findingId, lang, body.Rating, body.Comment, body.Author, ct);
                return Results.Ok(new NarrativeResponse(n.Text, n.Model, true, n.CreatedAtUtc, n.Rating, n.FeedbackComment));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        assessments.MapPut("/{id:guid}/business-rules/{ruleId:guid}/feedback", async (Guid id, Guid ruleId, FeedbackRequest body, AiFeedbackService service, CancellationToken ct, string? lang = null) =>
        {
            try
            {
                var rule = await service.RateBusinessRuleAsync(id, ruleId, body.Rating, body.Comment, body.Author, ct);
                return Results.Ok(ApiMapping.ToResponse(rule, lang?.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ?? false));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Business rules recovered by the model, per assessment.
        assessments.MapGet("/{id:guid}/business-rules", async (
            Guid id, IAssessmentRepository repository, IBusinessRuleRepository rules, IAiSettingsRepository aiSettings, CancellationToken ct, string? lang = null) =>
        {
            var assessment = await repository.GetAsync(id, ct);
            if (assessment is null)
            {
                return Results.NotFound();
            }

            var settings = await aiSettings.GetAsync(assessment.TenantId, ct);
            var analyses = await rules.ListAnalysesAsync(id, 10, ct);
            var items = await rules.ListAsync(id, ct);
            var pt = string.Equals(lang, "pt", StringComparison.OrdinalIgnoreCase) || (lang?.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ?? false);
            return Results.Ok(new BusinessRulesResponse(
                settings?.IsUsable ?? false,
                analyses.Select(ApiMapping.ToResponse).ToList(),
                items.Select(r => ApiMapping.ToResponse(r, pt)).ToList()));
        });

        assessments.MapPost("/{id:guid}/business-rules/analyze", async (Guid id, QueueBusinessRuleAnalysisHandler handler, CancellationToken ct) =>
        {
            try
            {
                var jobId = await handler.HandleAsync(id, ct);
                return Results.Accepted($"/api/assessments/{id}/business-rules", new RunQueuedResponse(jobId));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (AiNotConfiguredException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status412PreconditionFailed);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        assessments.MapGet("/{id:guid}/business-rules/export", async (
            Guid id, IAssessmentRepository repository, IBusinessRuleRepository rules, CancellationToken ct, string format = "csv", string? lang = null) =>
        {
            var assessment = await repository.GetAsync(id, ct);
            if (assessment is null)
            {
                return Results.NotFound();
            }

            var pt = lang?.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ?? false;
            var items = (await rules.ListAsync(id, ct)).Select(r => ApiMapping.ToResponse(r, pt)).ToList();
            var safeName = string.Concat(assessment.Name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')).ToLowerInvariant();
            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                return Results.File(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(items, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true }),
                    "application/json", $"{safeName}-business-rules.json");
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(pt ? "Arquivo,Membro,Linha,Regra,Descricao,Categoria,Condicoes,Confianca,Modelo" : "File,Member,Line,Rule,Description,Category,Conditions,Confidence,Model");
            // Same guard as the findings/compliance exports: neutralize spreadsheet formula injection.
            // Business-rule names/descriptions/conditions come from scanned code and AI output — untrusted.
            static string BrCsv(string value)
            {
                if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
                {
                    value = "'" + value;
                }

                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }

            foreach (var r in items)
            {
                sb.AppendLine(string.Join(",", new[] { r.FilePath, r.Symbol, r.StartLine.ToString(), r.Name, r.Description, r.Category, string.Join(" | ", r.Conditions), r.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), r.Model }
                    .Select(BrCsv)));
            }

            return Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", $"{safeName}-business-rules.csv");
        });
    }
}
