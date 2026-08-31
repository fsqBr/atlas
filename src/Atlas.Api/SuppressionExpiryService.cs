using Atlas.Application.Assessments;
using Atlas.Application.Findings;
using Atlas.Infrastructure.Persistence;

namespace Atlas.Api;

/// <summary>
/// Reopens suppressed findings whose waiver expired ("accepted for 90 days"): the finding goes
/// back to Open, and the assessment's health is recomputed. Runs hourly; expired suppression
/// policies need no sweep — they simply stop filtering candidates on the next run.
/// </summary>
public sealed class SuppressionExpiryService(IServiceScopeFactory scopeFactory, ILogger<SuppressionExpiryService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Suppression-expiry sweep failed; retrying in {Interval}.", Interval);
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

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<HttpTenantContext>().UseSystemScope();
        var findings = scope.ServiceProvider.GetRequiredService<IFindingRepository>();
        var inventory = scope.ServiceProvider.GetRequiredService<IInventoryRepository>();
        var health = scope.ServiceProvider.GetRequiredService<IHealthRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var expired = await findings.ListExpiredSuppressedAsync(DateTimeOffset.UtcNow, cancellationToken);
        if (expired.Count == 0)
        {
            return;
        }

        foreach (var finding in expired)
        {
            finding.Reopen();
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        foreach (var group in expired.GroupBy(f => f.AssessmentId))
        {
            var open = await findings.ListOpenAsync(group.Key, cancellationToken);
            var latestInventory = await inventory.GetLatestByAssessmentAsync(group.Key, cancellationToken);
            var latestHealth = await health.GetLatestAsync(group.Key, cancellationToken);
            health.Add(HealthSnapshotFactory.Create(
                group.First().TenantId, group.Key, latestHealth?.CommitSha, open,
                latestInventory.Sum(i => i.ProjectCount), runId: null));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Suppression expiry: {Count} finding(s) reopened across {Assessments} assessment(s).",
            expired.Count, expired.Select(f => f.AssessmentId).Distinct().Count());
    }
}
