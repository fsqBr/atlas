namespace Atlas.Domain.Workspaces;

/// <summary>
/// The only way scanners read workspace content. Implementations
/// must contain every access within the workspace root — path traversal and
/// symlink escapes are hostile input and must throw.
/// </summary>
public interface IArtifactReader
{
    string RootPath { get; }

    IEnumerable<string> EnumerateFiles(string searchPattern);

    Task<string> ReadAllTextAsync(string relativePath, CancellationToken cancellationToken);

    Stream OpenRead(string relativePath);
}
