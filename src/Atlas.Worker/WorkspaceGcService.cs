using Atlas.Application.Workspaces;
using Atlas.Infrastructure.Workspaces;
using Microsoft.Extensions.Options;

namespace Atlas.Worker;

/// <summary>
/// Periodic GC of expired/finished workspaces: a crashed worker or an
/// abandoned lease must never leave customer code on disk indefinitely.
/// </summary>
public sealed class WorkspaceGcService(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkspaceManagerOptions> options,
    ILogger<WorkspaceGcService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.GcInterval;
        logger.LogInformation("Workspace GC running every {Interval}.", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                using var scope = scopeFactory.CreateScope();
                var manager = scope.ServiceProvider.GetRequiredService<IWorkspaceManager>();
                await manager.CollectAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Workspace GC pass failed; retrying on next interval.");
            }
        }
    }
}
