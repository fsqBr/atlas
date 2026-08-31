using Atlas.Domain.Sources;
using Atlas.Domain.Workspaces;

namespace Atlas.Domain.Tests.Workspaces;

public class WorkspaceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly SourceReference Source = new(SourceReference.Kinds.LocalFolder, @"C:\code\repo");

    private static Workspace NewWorkspace(TimeSpan? lease = null) =>
        new(Guid.NewGuid(), TenantId, Source, @"C:\ws\x", lease ?? TimeSpan.FromHours(1));

    [Fact]
    public void Starts_preparing_with_active_lease()
    {
        var ws = NewWorkspace();

        Assert.Equal(WorkspaceState.Preparing, ws.State);
        Assert.False(ws.IsLeaseExpired());
        Assert.False(ws.IsCollectable());
    }

    [Fact]
    public void MarkReady_sets_root_borrowed_and_commit()
    {
        var ws = NewWorkspace();

        ws.MarkReady(@"C:\src\real", isBorrowed: true, commitSha: "abc123");

        Assert.Equal(WorkspaceState.Ready, ws.State);
        Assert.True(ws.IsBorrowed);
        Assert.Equal("abc123", ws.CommitSha);
        Assert.Equal(@"C:\src\real", ws.RootPath);
    }

    [Fact]
    public void Release_requires_ready()
    {
        var ws = NewWorkspace();

        Assert.Throws<InvalidOperationException>(ws.Release);
    }

    [Fact]
    public void Released_workspace_is_collectable_and_deletable()
    {
        var ws = NewWorkspace();
        ws.MarkReady(@"C:\ws\x", isBorrowed: false, commitSha: null);
        ws.Release();

        Assert.True(ws.IsCollectable());
        ws.MarkDeleted();
        Assert.Equal(WorkspaceState.Deleted, ws.State);
    }

    [Fact]
    public void Ready_with_active_lease_is_not_collectable_and_cannot_be_deleted()
    {
        var ws = NewWorkspace(TimeSpan.FromHours(1));
        ws.MarkReady(@"C:\ws\x", isBorrowed: false, commitSha: null);

        Assert.False(ws.IsCollectable());
        Assert.Throws<InvalidOperationException>(ws.MarkDeleted);
    }

    [Fact]
    public void Ready_with_expired_lease_is_collectable()
    {
        var ws = NewWorkspace(TimeSpan.FromMilliseconds(1));
        ws.MarkReady(@"C:\ws\x", isBorrowed: false, commitSha: null);

        Thread.Sleep(20);

        Assert.True(ws.IsLeaseExpired());
        Assert.True(ws.IsCollectable());
        ws.MarkDeleted();
        Assert.Equal(WorkspaceState.Deleted, ws.State);
    }

    [Fact]
    public void MarkFailed_records_reason_and_is_collectable()
    {
        var ws = NewWorkspace();
        ws.MarkFailed("clone failed");

        Assert.Equal(WorkspaceState.Failed, ws.State);
        Assert.Equal("clone failed", ws.FailureReason);
        Assert.True(ws.IsCollectable());
    }
}
