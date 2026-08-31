using Atlas.Connector.Abstractions;
using Atlas.Connector.Local;
using Atlas.Domain.Sources;
using Atlas.Domain.Workspaces;
using Atlas.Infrastructure.Persistence;
using Atlas.Infrastructure.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Atlas.Connector.Tests;

public class WorkspaceManagerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AtlasDbContext _db;
    private readonly string _workspaceRoot;
    private readonly string _sourceDir;

    public WorkspaceManagerTests()
    {
        // Ephemeral in-memory test database (allowed exception in the design notes).
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AtlasDbContext(new DbContextOptionsBuilder<AtlasDbContext>()
            .UseSqlite(_connection)
            .Options);
        _db.Database.EnsureCreated();

        _workspaceRoot = Directory.CreateTempSubdirectory("atlas-ws-root").FullName;
        _sourceDir = Directory.CreateTempSubdirectory("atlas-ws-source").FullName;
        File.WriteAllText(Path.Combine(_sourceDir, "a.cs"), "// code");
    }

    private WorkspaceManager NewManager(params ISourceConnector[] connectors) => new(
        _db,
        connectors,
        Options.Create(new WorkspaceManagerOptions
        {
            RootPath = _workspaceRoot,
            LeaseDuration = TimeSpan.FromMinutes(5),
        }),
        NullLogger<WorkspaceManager>.Instance);

    [Fact]
    public async Task Prepare_local_source_yields_ready_borrowed_workspace()
    {
        var manager = NewManager(new LocalFolderConnector());

        var workspace = await manager.PrepareAsync(
            new SourceReference(SourceReference.Kinds.LocalFolder, _sourceDir), CancellationToken.None);

        Assert.Equal(WorkspaceState.Ready, workspace.State);
        Assert.True(workspace.IsBorrowed);
        Assert.Equal(Path.GetFullPath(_sourceDir), workspace.RootPath);
    }

    [Fact]
    public async Task Unknown_source_kind_throws()
    {
        var manager = NewManager(new LocalFolderConnector());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.PrepareAsync(new SourceReference("svn", "svn://x"), CancellationToken.None));
    }

    [Fact]
    public async Task Failed_materialization_marks_failed_and_keeps_row()
    {
        var manager = NewManager(new LocalFolderConnector());

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            manager.PrepareAsync(
                new SourceReference(SourceReference.Kinds.LocalFolder, Path.Combine(_sourceDir, "missing")),
                CancellationToken.None));

        var stored = Assert.Single(_db.Workspaces.ToList());
        Assert.Equal(WorkspaceState.Failed, stored.State);
        Assert.NotNull(stored.FailureReason);
    }

    [Fact]
    public async Task Collect_deletes_owned_directory_but_never_borrowed_source()
    {
        var manager = NewManager(new LocalFolderConnector(), new FakeOwnedConnector());

        var borrowed = await manager.PrepareAsync(
            new SourceReference(SourceReference.Kinds.LocalFolder, _sourceDir), CancellationToken.None);
        var owned = await manager.PrepareAsync(
            new SourceReference("fake-owned", "anything"), CancellationToken.None);

        await manager.ReleaseAsync(borrowed.Id, CancellationToken.None);
        await manager.ReleaseAsync(owned.Id, CancellationToken.None);

        var collected = await manager.CollectAsync(CancellationToken.None);

        Assert.Equal(2, collected);
        Assert.True(Directory.Exists(_sourceDir), "borrowed user directory must never be deleted");
        Assert.False(Directory.Exists(owned.RootPath), "owned workspace directory must be deleted");
        Assert.All(_db.Workspaces.ToList(), w => Assert.Equal(WorkspaceState.Deleted, w.State));
    }

    /// <summary>Materializes an owned directory inside the managed root (like a git clone would).</summary>
    private sealed class FakeOwnedConnector : ISourceConnector
    {
        public ConnectorDescriptor Descriptor { get; } = new("connector.fake", "Fake", "0.0.1", []);

        public bool CanHandle(SourceReference source) => source.Kind == "fake-owned";

        public Task<IReadOnlyList<RepositoryInfo>> DiscoverRepositoriesAsync(
            SourceReference source, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RepositoryInfo>>([]);

        public Task<MaterializedSource> MaterializeAsync(
            SourceReference source, string targetDirectory, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(targetDirectory);
            File.WriteAllText(Path.Combine(targetDirectory, "cloned.txt"), "x");
            return Task.FromResult(new MaterializedSource(targetDirectory, IsBorrowed: false, CommitSha: "deadbeef"));
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try
        {
            Directory.Delete(_workspaceRoot, recursive: true);
            Directory.Delete(_sourceDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
