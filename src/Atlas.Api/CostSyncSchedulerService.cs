using Atlas.Application.Tenants;
using Atlas.Governance.Application;

namespace Atlas.Api;

/// <summary>Configuration and live state of the daily cost sync (Atlas:AiEstate:CostSync).</summary>
public sealed class CostSyncScheduleOptions
{
    public const string SectionName = "Atlas:AiEstate:CostSync";

    public bool Enabled { get; set; } = true;

    /// <summary>UTC hour of the daily run (0–23). Billing APIs settle overnight; 06:00 UTC catches the previous day.</summary>
    public int HourUtc { get; set; } = 6;

    /// <summary>Days pulled per run; the window is replaced per provider, so re-pulling is safe.</summary>
    public int Days { get; set; } = 7;
}

public sealed class CostSyncScheduleState
{
    public DateTimeOffset? LastRunUtc { get; set; }

    public DateTimeOffset? NextRunUtc { get; set; }

    public int LastTenants { get; set; }

    public int LastSources { get; set; }

    public int LastFailures { get; set; }
}

/// <summary>
/// Pulls billing data once a day for every tenant that has enabled cost sources, so reconciliation and the
/// calibrated forecast build their seven-day overlap without anyone clicking "Sync now". Per tenant, per source,
/// isolated failures (the sync service already records the last error on the source).
/// </summary>
public sealed class CostSyncSchedulerService(IServiceScopeFactory scopeFactory, CostSyncScheduleOptions options, CostSyncScheduleState state, ILogger<CostSyncSchedulerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Daily cost sync disabled (Atlas:AiEstate:CostSync:Enabled=false).");
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        while (!stoppingToken.IsCancellationRequested)
        {
            var next = NextRun(DateTimeOffset.UtcNow, Math.Clamp(options.HourUtc, 0, 23));
            state.NextRunUtc = next;
            try
            {
                await Task.Delay(next - DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await RunOnceAsync(stoppingToken);
        }
    }

    internal static DateTimeOffset NextRun(DateTimeOffset now, int hourUtc)
    {
        var today = new DateTimeOffset(now.Year, now.Month, now.Day, hourUtc, 0, 0, TimeSpan.Zero);
        return today > now ? today : today.AddDays(1);
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var tenantsDone = 0;
        var sourcesDone = 0;
        var failures = 0;
        try
        {
            IReadOnlyList<Atlas.Domain.Tenants.Tenant> tenants;
            using (var listScope = scopeFactory.CreateScope())
            {
                listScope.ServiceProvider.GetRequiredService<HttpTenantContext>().UseSystemScope();
                tenants = await listScope.ServiceProvider.GetRequiredService<ITenantRepository>().ListAsync(cancellationToken);
            }

            foreach (var tenant in tenants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var scope = scopeFactory.CreateScope();
                scope.ServiceProvider.GetRequiredService<HttpTenantContext>().Set(tenant.Id, tenant.Name);
                var sources = await scope.ServiceProvider.GetRequiredService<ICostSourceRepository>().ListAsync(cancellationToken);
                if (!sources.Any(s => s.Enabled))
                {
                    continue;
                }

                tenantsDone++;
                var results = await scope.ServiceProvider.GetRequiredService<CostSyncService>().SyncAsync(Math.Clamp(options.Days, 1, 90), cancellationToken);
                sourcesDone += results.Count;
                failures += results.Count(r => !r.Succeeded);
                foreach (var r in results.Where(r => !r.Succeeded))
                {
                    logger.LogWarning("Daily cost sync: tenant {Tenant} provider {Provider} failed: {Error}", tenant.Name, r.Provider, r.Error);
                }
            }

            logger.LogInformation("Daily cost sync: {Tenants} tenant(s), {Sources} source(s), {Failures} failure(s).", tenantsDone, sourcesDone, failures);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            failures++;
            logger.LogError(ex, "Daily cost sync failed; next attempt tomorrow.");
        }

        state.LastRunUtc = DateTimeOffset.UtcNow;
        state.LastTenants = tenantsDone;
        state.LastSources = sourcesDone;
        state.LastFailures = failures;
    }
}
