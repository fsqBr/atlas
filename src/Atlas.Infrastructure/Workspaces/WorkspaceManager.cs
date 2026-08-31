using Atlas.Application.Workspaces;
using Atlas.Connector.Abstractions;
using Atlas.Domain.Sources;
using Atlas.Domain.Tenants;
using Atlas.Domain.Workspaces;
using Atlas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Atlas.Infrastructure.Workspaces;

public sealed class WorkspaceManager(
    AtlasDbContext db,
    IEnumerable<ISourceConnector> connectors,
    IOptions<WorkspaceManagerOptions> options,
    ILogger<WorkspaceManager> logger) : IWorkspaceManager
{
    private readonly WorkspaceManagerOptions _options = options.Value;

    public async Task<Workspace> PrepareAsync(SourceReference source, CancellationToken cancellationToken)
    {
        var connector = connectors.FirstOrDefault(c => c.CanHandle(source))
            ?? throw new InvalidOperationException(
                $"No connector registered for source kind '{source.Kind}'.");

        var id = Guid.NewGuid();
        var targetDirectory = Path.Combine(Path.GetFullPath(_options.RootPath), id.ToString("N"));

        var workspace = new Workspace(
            id,
            WellKnownTenants.DefaultId,
            source,
            targetDirectory,
            _options.LeaseDuration);

        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var materialized = await connector.MaterializeAsync(source, targetDirectory, cancellationToken);
            workspace.MarkReady(materialized.RootPath, materialized.IsBorrowed, materialized.CommitSha, materialized.History);
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Workspace {WorkspaceId} ready via {ConnectorId} (borrowed: {IsBorrowed}, commit: {CommitSha})",
                workspace.Id, connector.Descriptor.Id, materialized.IsBorrowed, materialized.CommitSha ?? "n/a");

            return workspace;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            workspace.MarkFailed(ex.Message);
            await db.SaveChangesAsync(CancellationToken.None);
            TryDeleteOwnedDirectory(targetDirectory);
            throw;
        }
    }

    public async Task ReleaseAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var workspace = await db.Workspaces.SingleAsync(w => w.Id == workspaceId, cancellationToken);
        workspace.Release();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> CollectAsync(CancellationToken cancellationToken)
    {
        var candidates = await db.Workspaces
            .Where(w => w.State != WorkspaceState.Deleted)
            .ToListAsync(cancellationToken);

        var collected = 0;
        foreach (var workspace in candidates.Where(w => w.IsCollectable()))
        {
            if (!workspace.IsBorrowed)
            {
                TryDeleteOwnedDirectory(workspace.RootPath);
            }

            workspace.MarkDeleted();
            collected++;
        }

        if (collected > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Workspace GC collected {Count} workspace(s).", collected);
        }

        return collected;
    }

    /// <summary>
    /// Deletes a directory only when it lives under the managed workspace root —
    /// a borrowed or mislabeled path outside the root is never touched.
    /// </summary>
    private void TryDeleteOwnedDirectory(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_options.RootPath));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Refusing to delete '{Path}': outside the managed workspace root '{Root}'.", full, root);
            return;
        }

        try
        {
            if (Directory.Exists(full))
            {
                Directory.Delete(full, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to delete workspace directory '{Path}'; next GC pass retries.", full);
        }
    }
}

public sealed class WorkspaceManagerOptions
{
    public const string SectionName = "Atlas:Workspaces";

    public string RootPath { get; set; } = Path.Combine(Path.GetTempPath(), "atlas-workspaces");

    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromHours(2);

    public TimeSpan GcInterval { get; set; } = TimeSpan.FromMinutes(5);
}
