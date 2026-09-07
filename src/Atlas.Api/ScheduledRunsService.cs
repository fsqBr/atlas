using Atlas.Application.Assessments;
using Atlas.Domain.Assessments;

namespace Atlas.Api;

/// <summary>
/// Re-runs assessments on their own cadence (Assessment.RerunEveryDays): every
/// few minutes it queues a run for each assessment whose last completion is
/// older than its interval and that has no job pending. Continuous
/// Intelligence starts here, on the existing queue.
/// </summary>
public sealed class ScheduledRunsService(IServiceScopeFactory scopeFactory, ILogger<ScheduledRunsService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Scheduled runs tick failed; retrying in {Interval}.", Interval);
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<HttpTenantContext>().UseSystemScope();
        var assessments = scope.ServiceProvider.GetRequiredService<IAssessmentRepository>();
        var runAgain = scope.ServiceProvider.GetRequiredService<RunAgainHandler>();
        var now = DateTimeOffset.UtcNow;

        foreach (var assessment in await assessments.ListRecentAsync(500, cancellationToken))
        {
            if (!IsDue(assessment, now))
            {
                continue;
            }

            try
            {
                await runAgain.HandleAsync(assessment.Id, cancellationToken);
                logger.LogInformation("Scheduled re-run queued for {Assessment} (every {Days} day(s)).", assessment.Name, assessment.RerunEveryDays);
            }
            catch (InvalidOperationException)
            {
                // A job is already pending: the next tick will see it as active and skip.
            }
        }
    }

    /// <summary>Due when a cadence is set, the assessment is idle, and the last completion (or creation) is older than the cadence.</summary>
    public static bool IsDue(Assessment assessment, DateTimeOffset now)
    {
        if (assessment.RerunEveryDays is not { } days || days <= 0)
        {
            return false;
        }

        if (assessment.Status is AssessmentStatus.Created or AssessmentStatus.Running)
        {
            return false;
        }

        var last = assessment.CompletedAtUtc ?? assessment.CreatedAtUtc;
        return now - last >= TimeSpan.FromDays(days);
    }
}
