using Atlas.Connector.Local;
using Atlas.Domain.Sources;

namespace Atlas.Connector.Tests;

public class LocalFolderConnectorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("atlas-local-test").FullName;
    private readonly LocalFolderConnector _connector = new();

    [Fact]
    public async Task Discovers_git_repositories_under_root()
    {
        Directory.CreateDirectory(Path.Combine(_root, "repo-a", ".git"));
        Directory.CreateDirectory(Path.Combine(_root, "nested", "repo-b", ".git"));

        var repos = await _connector.DiscoverRepositoriesAsync(
            new SourceReference(SourceReference.Kinds.LocalFolder, _root), CancellationToken.None);

        Assert.Equal(2, repos.Count);
        Assert.Contains(repos, r => r.Name == "repo-a");
        Assert.Contains(repos, r => r.Name == "repo-b");
    }

    [Fact]
    public async Task Plain_folder_discovers_itself()
    {
        var repos = await _connector.DiscoverRepositoriesAsync(
            new SourceReference(SourceReference.Kinds.LocalFolder, _root), CancellationToken.None);

        var repo = Assert.Single(repos);
        Assert.Equal(Path.GetFileName(_root), repo.Name);
    }

    [Fact]
    public async Task Materializes_as_borrowed_in_place()
    {
        var target = Path.Combine(_root, "unused-target");

        var materialized = await _connector.MaterializeAsync(
            new SourceReference(SourceReference.Kinds.LocalFolder, _root), target, CancellationToken.None);

        Assert.True(materialized.IsBorrowed);
        Assert.Equal(Path.GetFullPath(_root), materialized.RootPath);
        Assert.Null(materialized.CommitSha);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task Missing_directory_throws()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            _connector.MaterializeAsync(
                new SourceReference(SourceReference.Kinds.LocalFolder, Path.Combine(_root, "nope")),
                Path.Combine(_root, "t"),
                CancellationToken.None));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
