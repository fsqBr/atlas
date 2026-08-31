using Atlas.Domain.Sources;
using Atlas.Domain.Workspaces;

namespace Atlas.Application.Workspaces;

/// <summary>
/// Port for the workspace lifecycle (009): prepare a source into a
/// leased workspace, release it when done, collect expired/finished ones.
/// </summary>
public interface IWorkspaceManager
{
    Task<Workspace> PrepareAsync(SourceReference source, CancellationToken cancellationToken);

    Task ReleaseAsync(Guid workspaceId, CancellationToken cancellationToken);

    /// <summary>GC pass: deletes owned directories of collectable workspaces. Returns count collected.</summary>
    Task<int> CollectAsync(CancellationToken cancellationToken);
}
