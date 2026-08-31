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
    // Renew comfortably inside the lease so a long scan keeps its claim; two missed beats still
    // leave margin before the 30-minute expiry.
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(10);

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
            var tenantSettings = await services.GetRequiredService<Atlas.Application.Assessments.ITenantNotificationSettingsRepository>()
                .GetForTenantAsync(assessment.TenantId, cancellationToken);
            var overrides = tenantSettings is null
                ? null
                : new TenantNotificationOverrides(tenantSettings.WebhookUrl, tenantSettings.Secret, tenantSettings.SlackWebhookUrl, tenantSettings.TeamsWebhookUrl);
            var payload = RunNotifier.BuildPayload(assessment.Name, assessment.Id, RunDiff.Summarize(ordered[0]),
                ordered.Skip(1).FirstOrDefault(r => r.Status is Atlas.Domain.Assessments.AssessmentRunStatus.Completed or Atlas.Domain.Assessments.AssessmentRunStatus.CompletedWithWarnings) is { } prev ? RunDiff.Summarize(prev) : null, options.PublicBaseUrl, assessment.TargetScore, assessment.TargetDate);
            await notifier.NotifyAsync(payload, assessment.WebhookUrl, cancellationToken, overrides);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Run notification failed for assessment {AssessmentId}.", assessmentId);
        }
    }

    /// <summary>
    /// Renews the job's lease every <see cref="HeartbeatInterval"/> while <paramref name="work"/>
    /// runs, on its own scope/connection (the job's DbContext is single-threaded). A scan that
    /// outlives the 30-minute lease is no longer eligible for reclaim by another replica — which
    /// would otherwise run it a second time. If the lease is lost anyway (clock skew, a paused
    /// replica), the work is cancelled so it cannot double-write.
    /// </summary>
    private async Task WithHeartbeatAsync(Guid jobId, Func<CancellationToken, Task> work, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = HeartbeatLoopAsync(jobId, linked);
        try
        {
            await work(linked.Token);
        }
        finally
        {
            if (!linked.IsCancellationRequested)
            {
                linked.Cancel();
            }

            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException)
            {
                // expected when we cancel the loop on completion
            }
        }
    }

    private async Task HeartbeatLoopAsync(Guid jobId, CancellationTokenSource linked)
    {
        while (!linked.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, linked.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                using var scope = scopeFactory.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<IScanJobQueue>();
                if (!await queue.HeartbeatAsync(jobId, _workerId, LeaseDuration, linked.Token))
                {
                    // Another replica already reclaimed the row: abandon our run so we don't
                    // both write results for the same assessment.
                    logger.LogWarning("Job {JobId} lease lost (reclaimed elsewhere); cancelling this run.", jobId);
                    await linked.CancelAsync();
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Heartbeat for job {JobId} failed; will retry next interval.", jobId);
            }
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
                await WithHeartbeatAsync(job.Id, ct => analysis.RunAsync(job.AssessmentId, ct), stoppingToken);
                job.Succeed();
                logger.LogInformation("Job {JobId} (AI business rules) succeeded.", job.Id);
            }
            else if (job.Kind == ScanJob.Kinds.FindingFix)
            {
                var fixer = scope.ServiceProvider.GetRequiredService<FindingFixRunner>();
                await WithHeartbeatAsync(job.Id, ct => fixer.RunAsync(job.AssessmentId, job.Payload, ct), stoppingToken);
                job.Succeed();
                logger.LogInformation("Job {JobId} (AI fix suggestion) succeeded.", job.Id);
            }
            else
            {
                var runner = scope.ServiceProvider.GetRequiredService<AssessmentRunner>();
                await WithHeartbeatAsync(job.Id, ct => runner.RunAsync(job.AssessmentId, ct), stoppingToken);
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
