using System.Text.Json;
using Atlas.Application.Assessments;
using Atlas.Application.Workspaces;
using Atlas.Domain.Ai;
using Atlas.Domain.Jobs;
using Atlas.Domain.Workspaces;
using Atlas.Language.Abstractions;
using Microsoft.Extensions.Logging;

namespace Atlas.Application.Ai;

/// <summary>
/// Worker side of "Analyze with AI": materializes the assessment source, picks
/// the decision-heavy methods, sends them in batches to the configured model and
/// stores the recovered rules. Refuses to run when AI is disabled — nothing ever
/// leaves the environment by accident.
/// </summary>
public sealed class BusinessRuleAnalysisRunner(
    IAssessmentRepository assessments,
    IBusinessRuleRepository rules,
    AiSettingsService aiSettings,
    IWorkspaceManager workspaces,
    IBusinessRuleCandidateSource candidates,
    BusinessRuleExtractor extractor,
    IUnitOfWork unitOfWork,
    ILogger<BusinessRuleAnalysisRunner> logger)
{
    public async Task RunAsync(Guid assessmentId, CancellationToken cancellationToken)
    {
        var assessment = await assessments.GetAsync(assessmentId, cancellationToken)
            ?? throw new KeyNotFoundException($"Assessment {assessmentId} not found.");

        var resolved = await aiSettings.ResolveClientAsync(cancellationToken)
            ?? throw new InvalidOperationException("AI analysis is disabled or has no usable provider; configure it under Settings → AI.");
        var (settings, client) = resolved;

        var analysis = new BusinessRuleAnalysis(Guid.NewGuid(), assessment.TenantId, assessment.Id, settings.Provider, settings.Model);
        rules.AddAnalysis(analysis);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        Workspace? workspace = null;
        var found = 0;
        ExtractionOutcome? outcome = null;
        try
        {
            workspace = await workspaces.PrepareAsync(assessment.Source, cancellationToken);
            var reader = new WorkspaceReader(workspace.RootPath);
            var picked = await candidates.FindAsync(reader, settings.MaxSnippetsPerAnalysis, cancellationToken);
            found = picked.Count;
            logger.LogInformation("Business rule analysis {AnalysisId}: {Count} candidate methods for assessment {AssessmentId} via {Provider}/{Model}.",
                analysis.Id, found, assessment.Id, settings.Provider, settings.Model);

            outcome = await extractor.ExtractAsync(client, picked, cancellationToken);

            var entities = outcome.Rules.Select(r => new BusinessRule(
                Guid.NewGuid(), assessment.TenantId, assessment.Id, analysis.Id,
                r.FilePath, r.Symbol, r.StartLine, r.Name, r.DescriptionEn, r.DescriptionPt,
                r.Category, JsonSerializer.Serialize(r.Conditions), r.Confidence, client.Model)).ToList();

            await rules.ReplaceAsync(assessment.Id, entities, cancellationToken);
            analysis.Complete(found, outcome.SnippetsSent, entities.Count, outcome.InputTokens, outcome.OutputTokens);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Business rule analysis {AnalysisId} completed: {Rules} rules from {Sent} snippets ({In}/{Out} tokens).",
                analysis.Id, entities.Count, outcome.SnippetsSent, outcome.InputTokens, outcome.OutputTokens);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            analysis.Fail(ex.Message, found, outcome?.SnippetsSent ?? 0, outcome?.InputTokens ?? 0, outcome?.OutputTokens ?? 0);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (workspace is not null)
            {
                await workspaces.ReleaseAsync(workspace.Id, CancellationToken.None);
            }
        }
    }
}

/// <summary>API side: validates that AI is usable and queues the job on the existing queue.</summary>
public sealed class QueueBusinessRuleAnalysisHandler(
    IAssessmentRepository assessments,
    IAiSettingsRepository aiSettings,
    IScanJobQueue queue,
    IUnitOfWork unitOfWork)
{
    public async Task<Guid> HandleAsync(Guid assessmentId, CancellationToken cancellationToken)
    {
        var assessment = await assessments.GetAsync(assessmentId, cancellationToken)
            ?? throw new KeyNotFoundException($"Assessment {assessmentId} not found.");

        var settings = await aiSettings.GetAsync(assessment.TenantId, cancellationToken);
        if (settings is null || !settings.IsUsable)
        {
            throw new AiNotConfiguredException();
        }

        if (await queue.HasActiveJobAsync(assessmentId, cancellationToken))
        {
            throw new InvalidOperationException("A job is already queued or in progress for this assessment.");
        }

        var job = new ScanJob(Guid.NewGuid(), assessment.TenantId, assessment.Id, ScanJob.Kinds.BusinessRules);
        queue.Enqueue(job);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return job.Id;
    }
}

public sealed class AiNotConfiguredException() : Exception("AI analysis is not enabled. An administrator must configure a provider and key under Settings → AI.");
