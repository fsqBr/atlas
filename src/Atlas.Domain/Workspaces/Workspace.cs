using Atlas.Domain.Sources;

namespace Atlas.Domain.Workspaces;

/// <summary>
/// A materialized, normalized view of a source that scanners consume.
/// Lifecycle and lease semantics workspaces are leased, renewed by
/// their consumers and garbage-collected when the lease expires. Borrowed
/// workspaces point at user-owned directories and are never deleted from disk.
/// </summary>
public sealed class Workspace
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string SourceKind { get; private set; } = null!;
    public string SourceLocator { get; private set; } = null!;
    public string? Branch { get; private set; }
    public string? CommitSha { get; private set; }
    public string RootPath { get; private set; } = null!;
    public bool IsBorrowed { get; private set; }
    public WorkspaceState State { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset LeaseExpiresAtUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Change history read by the connector for this materialization (not persisted; feeds change-risk rules).</summary>
    public IReadOnlyList<FileChangeFact> History { get; private set; } = [];

    private Workspace()
    {
    }

    public Workspace(Guid id, Guid tenantId, SourceReference source, string rootPath, TimeSpan leaseDuration)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must not be empty.", nameof(id));
        }

        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("Root path must not be empty.", nameof(rootPath));
        }

        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentException("Lease duration must be positive.", nameof(leaseDuration));
        }

        ArgumentNullException.ThrowIfNull(source);

        Id = id;
        TenantId = tenantId;
        SourceKind = source.Kind;
        SourceLocator = source.Locator;
        Branch = source.Branch;
        RootPath = rootPath;
        State = WorkspaceState.Preparing;
        CreatedAtUtc = DateTimeOffset.UtcNow;
        LeaseExpiresAtUtc = CreatedAtUtc.Add(leaseDuration);
    }

    public void MarkReady(string rootPath, bool isBorrowed, string? commitSha, IReadOnlyList<FileChangeFact>? history = null)
    {
        EnsureState(WorkspaceState.Preparing);
        RootPath = rootPath;
        IsBorrowed = isBorrowed;
        CommitSha = commitSha;
        History = history ?? [];
        State = WorkspaceState.Ready;
    }

    public void MarkFailed(string reason)
    {
        EnsureState(WorkspaceState.Preparing);
        FailureReason = reason;
        State = WorkspaceState.Failed;
    }

    public void RenewLease(TimeSpan duration)
    {
        EnsureState(WorkspaceState.Ready);
        LeaseExpiresAtUtc = DateTimeOffset.UtcNow.Add(duration);
    }

    public void Release()
    {
        EnsureState(WorkspaceState.Ready);
        State = WorkspaceState.Released;
    }

    public void MarkDeleted()
    {
        if (State is not (WorkspaceState.Released or WorkspaceState.Failed) && !IsLeaseExpired())
        {
            throw new InvalidOperationException(
                $"Workspace {Id} cannot be deleted in state {State} with an active lease.");
        }

        State = WorkspaceState.Deleted;
    }

    public bool IsLeaseExpired() => DateTimeOffset.UtcNow > LeaseExpiresAtUtc;

    /// <summary>Eligible for GC: lease expired or explicitly finished, and not user-owned disk.</summary>
    public bool IsCollectable() =>
        State is WorkspaceState.Released or WorkspaceState.Failed
        || (State == WorkspaceState.Ready && IsLeaseExpired());

    private void EnsureState(WorkspaceState expected)
    {
        if (State != expected)
        {
            throw new InvalidOperationException(
                $"Workspace {Id} is in state {State}; expected {expected}.");
        }
    }
}

public enum WorkspaceState
{
    Preparing,
    Ready,
    Released,
    Failed,
    Deleted,
}
