using Atlas.Domain.Workspaces;

namespace Atlas.Application.Workspaces;

/// <summary>Minimal read-only view over a materialized workspace; every path is contained to the root.</summary>
public sealed class WorkspaceReader(string rootPath) : IArtifactReader
{
    public string RootPath => rootPath;

    public IEnumerable<string> EnumerateFiles(string searchPattern) =>
        Directory.EnumerateFiles(rootPath, searchPattern, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Select(f => Path.GetRelativePath(rootPath, f).Replace('\\', '/'));

    public Task<string> ReadAllTextAsync(string relativePath, CancellationToken cancellationToken) =>
        File.ReadAllTextAsync(Contain(relativePath), cancellationToken);

    public Stream OpenRead(string relativePath) => File.OpenRead(Contain(relativePath));

    public bool Exists(string relativePath) => File.Exists(Contain(relativePath));

    private string Contain(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.Ordinal) ? full : throw new UnauthorizedAccessException($"Path '{relativePath}' escapes the workspace.");
    }
}
