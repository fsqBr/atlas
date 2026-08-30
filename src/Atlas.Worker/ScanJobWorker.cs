using Atlas.Application.Ai;
using Atlas.Application.Assessments;
using Atlas.Domain.Jobs;

namespace Atlas.Worker;

/// <summary>
/// Claims scan jobs from the Postgres queue and runs assessments.
/// One job at a time per worker instance; scale by adding worker replicas —
/// SKIP LOCKED keeps them from colliding. Child-process isolation per scanner
/// arrives with the first heavyweight scanner.
/// </summary>
public sealed class ScanJobWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<ScanJobWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(30);

    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Atlas worker {WorkerId} started; polling scan jobs every {Interval}.", _workerId, PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            var ranJob = false;
            try
            {
                ranJob = await ClaimAndRunOneAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job loop iteration failed; continuing.");
            }

            if (!ranJob)
            {
                try
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        logger.LogInformation("Atlas worker {WorkerId} stopping.", _workerId);
    }

    /// <summary>Webhooks are best effort and never fail the job.</summary>
    private async Task NotifyAsync(IServiceProvider services, Guid assessmentId, CancellationToken cancellationToken)
    {
        try
        {
            var assessment = await services.GetRequiredService<IAssessmentRepository>().GetAsync(assessmentId, cancellationToken);
            var runs = await services.GetRequiredService<IAssessmentRunRepository>().ListByAssessmentAsync(assessmentId, cancellationToken);
            var ordered = runs.OrderByDescending(r => r.Number).ToList();
            if (assessment is null || ordered.Count == 0)
            {
                return;
            }

            var notifier = services.GetRequiredService<RunNotifier>();
            var options = services.GetRequiredService<NotificationOptions>();
            var payload = RunNotifier.BuildPayload(assessment.Name, assessment.Id, RunDiff.Summarize(ordered[0]),
                ordered.Count > 1 ? RunDiff.Summarize(ordered[1]) : null, options.PublicBaseUrl, assessment.TargetScore, assessment.TargetDate);
            await notifier.NotifyAsync(payload, assessment.WebhookUrl, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Run notification failed for assessment {AssessmentId}.", assessmentId);
        }
    }

    private async Task<bool> ClaimAndRunOneAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IScanJobQueue>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var job = await queue.ClaimAsync(_workerId, LeaseDuration, stoppingToken);
        if (job is null)
        {
            return false;
        }

        job.Start();
        await unitOfWork.SaveChangesAsync(stoppingToken);
        logger.LogInformation("Job {JobId} [{Kind}] started for assessment {AssessmentId} (attempt {Attempt}).",
            job.Id, job.Kind, job.AssessmentId, job.Attempt);

        try
        {
            if (job.Kind == ScanJob.Kinds.BusinessRules)
            {
                var analysis = scope.ServiceProvider.GetRequiredService<BusinessRuleAnalysisRunner>();
                await analysis.RunAsync(job.AssessmentId, stoppingToken);
                job.Succeed();
                logger.LogInformation("Job {JobId} (AI business rules) succeeded.", job.Id);
            }
            else if (job.Kind == ScanJob.Kinds.FindingFix)
            {
                var fixer = scope.ServiceProvider.GetRequiredService<FindingFixRunner>();
                await fixer.RunAsync(job.AssessmentId, job.Payload, stoppingToken);
                job.Succeed();
                logger.LogInformation("Job {JobId} (AI fix suggestion) succeeded.", job.Id);
            }
            else
            {
                var runner = scope.ServiceProvider.GetRequiredService<AssessmentRunner>();
                await runner.RunAsync(job.AssessmentId, stoppingToken);
                job.Succeed();
                logger.LogInformation("Job {JobId} succeeded.", job.Id);
                await NotifyAsync(scope.ServiceProvider, job.AssessmentId, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            job.Fail("Worker shutting down.");
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId} failed.", job.Id);
            job.Fail(ex.Message);
        }

        await unitOfWork.SaveChangesAsync(CancellationToken.None);
        return true;
    }
}
