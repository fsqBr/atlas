using Atlas.Connector.Abstractions;
using Atlas.Connector.Git;
using Atlas.Domain.Sources;

namespace Atlas.Connector.Local;

/// <summary>
/// Materializes a local directory as a borrowed workspace: Atlas reads it in
/// place and never deletes or writes to it. Discovery lists git
/// repositories under the root, or the root itself when it is a plain folder.
/// </summary>
public sealed class LocalFolderConnector(GitHistoryReader? history = null) : ISourceConnector
{
    public ConnectorDescriptor Descriptor { get; } = new(
        Id: "connector.local",
        Name: "Local Folder",
        Version: "0.1.0",
        Capabilities: ["discover", "materialize"]);

    public bool CanHandle(SourceReference source) =>
        source.Kind == SourceReference.Kinds.LocalFolder;

    public Task<IReadOnlyList<RepositoryInfo>> DiscoverRepositoriesAsync(
        SourceReference source,
        CancellationToken cancellationToken)
    {
        var root = ResolveExistingDirectory(source.Locator);

        var gitRepos = Directory
            .EnumerateDirectories(root, ".git", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 4,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System, // dot-directories are "hidden" on Unix — .git must still be found
            })
            .Select(gitDir => Path.GetDirectoryName(gitDir)!)
            .Select(repoRoot => new RepositoryInfo(
                Name: Path.GetFileName(repoRoot),
                Locator: repoRoot,
                Kind: SourceReference.Kinds.LocalFolder))
            .ToList();

        IReadOnlyList<RepositoryInfo> result = gitRepos.Count > 0
            ? gitRepos
            : [new RepositoryInfo(Path.GetFileName(root), root, SourceReference.Kinds.LocalFolder)];

        return Task.FromResult(result);
    }

    public async Task<MaterializedSource> MaterializeAsync(
        SourceReference source,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        var root = ResolveExistingDirectory(source.Locator);

        // Borrowed: the user's directory is used in place; targetDirectory stays unused. If it is a git working
        // copy, read its history (read-only) so change-risk rules have data.
        var changes = history is { Enabled: true } ? await history.ReadAsync(root, cancellationToken) : [];
        return new MaterializedSource(root, IsBorrowed: true, CommitSha: null, History: changes);
    }

    private static string ResolveExistingDirectory(string locator)
    {
        var full = Path.GetFullPath(locator);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"Local source directory not found: {full}");
        }

        return full;
    }
}
