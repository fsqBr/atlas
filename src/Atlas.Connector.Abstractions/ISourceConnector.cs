using Atlas.Domain.Sources;

namespace Atlas.Connector.Abstractions;

/// <summary>
/// Connector contract. Connectors are the only code
/// aware of a provider; they materialize a SourceReference into a directory the
/// workspace manager owns. Adding a provider means a new package implementing
/// this interface plus registration — never a change to scanners or the core.
/// </summary>
public interface ISourceConnector
{
    ConnectorDescriptor Descriptor { get; }

    bool CanHandle(SourceReference source);

    Task<IReadOnlyList<RepositoryInfo>> DiscoverRepositoriesAsync(
        SourceReference source,
        CancellationToken cancellationToken);

    Task<MaterializedSource> MaterializeAsync(
        SourceReference source,
        string targetDirectory,
        CancellationToken cancellationToken);
}

public sealed record ConnectorDescriptor(
    string Id,
    string Name,
    string Version,
    IReadOnlyCollection<string> Capabilities);

/// <summary>
/// A repository a connector can materialize. Locator is what goes into
/// SourceReference.Locator for that Kind. Optional metadata helps a user pick
/// (archived/disabled repositories are usually skipped).
/// </summary>
public sealed record RepositoryInfo(
    string Name,
    string Locator,
    string Kind,
    string? DefaultBranch = null,
    bool Archived = false,
    string? Language = null,
    DateTimeOffset? LastPushUtc = null,
    bool IsPrivate = false);

/// <summary>
/// Result of materializing a source. Borrowed sources live in user-owned
/// directories and must never be deleted by workspace cleanup.
/// </summary>
public sealed record MaterializedSource(string RootPath, bool IsBorrowed, string? CommitSha, IReadOnlyList<Atlas.Domain.Workspaces.FileChangeFact>? History = null);
