using System.IO.Enumeration;
using Atlas.Domain.Workspaces;

namespace Atlas.Scanner.Runtime;

/// <summary>
/// IArtifactReader that refuses any access outside the workspace root:
/// relative traversal (..), absolute paths pointing elsewhere, and symlinks whose
/// resolved target escapes the root all throw. Workspace content is hostile input.
/// The directory tree is walked once (pruning build output, package caches and
/// VCS internals) and cached: every analyzer and scanner pattern query is then
/// answered from memory — on bind/network mounts a traversal costs seconds per pass.
/// </summary>
public sealed class ContainedArtifactReader : IArtifactReader
{
    private static readonly EnumerationOptions TopLevel = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private readonly string _root;
    private readonly Lazy<IReadOnlyList<string>> _allFiles;
    private readonly PathExclusions _exclusions;

    public ContainedArtifactReader(string rootPath)
        : this(rootPath, excludeGlobs: null)
    {
    }

    /// <param name="excludeGlobs">Per-assessment globs; combined with PathExclusions.DefaultGlobs and the root .atlasignore.</param>
    public ContainedArtifactReader(string rootPath, IReadOnlyList<string>? excludeGlobs)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("Root path must not be empty.", nameof(rootPath));
        }

        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        if (!Directory.Exists(_root))
        {
            throw new DirectoryNotFoundException($"Workspace root not found: {_root}");
        }

        var ignoreFile = Path.Combine(_root, PathExclusions.IgnoreFileName);
        var fromFile = File.Exists(ignoreFile) ? PathExclusions.ParseIgnoreFile(SafeRead(ignoreFile)) : [];
        _exclusions = PathExclusions.Compile(fromFile.Concat(excludeGlobs ?? []));
        _allFiles = new Lazy<IReadOnlyList<string>>(Walk, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Effective exclusion globs (defaults + .atlasignore + assessment).</summary>
    public IReadOnlyList<string> ExcludeGlobs => _exclusions.Globs;

    private static string? SafeRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public string RootPath => _root;

    public IEnumerable<string> EnumerateFiles(string searchPattern)
    {
        var files = _allFiles.Value;
        if (searchPattern is "*" or "*.*" or "")
        {
            return files;
        }

        return files.Where(f => FileSystemName.MatchesSimpleExpression(searchPattern, Path.GetFileName(f), ignoreCase: true));
    }

    public Task<string> ReadAllTextAsync(string relativePath, CancellationToken cancellationToken) =>
        File.ReadAllTextAsync(Contain(relativePath), cancellationToken);

    public Stream OpenRead(string relativePath) =>
        new FileStream(Contain(relativePath), FileMode.Open, FileAccess.Read, FileShare.Read);

    private IReadOnlyList<string> Walk()
    {
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(_root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            List<string> files;
            List<string> subdirectories;
            try
            {
                files = Directory.EnumerateFiles(directory, "*", TopLevel).ToList();
                subdirectories = Directory.EnumerateDirectories(directory, "*", TopLevel).ToList();
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (IsContained(file))
                {
                    var relative = Path.GetRelativePath(_root, file);
                    if (!_exclusions.IsExcluded(relative))
                    {
                        result.Add(relative);
                    }
                }
            }

            foreach (var subdirectory in subdirectories)
            {
                var name = Path.GetFileName(subdirectory);
                if (WorkspaceFilters.ShouldPruneDirectory(name))
                {
                    continue;
                }

                if (name.Equals("packages", StringComparison.OrdinalIgnoreCase) && WorkspaceFilters.IsNuGetPackagesFolder(subdirectory))
                {
                    continue;
                }

                if (IsContained(subdirectory) && !_exclusions.IsDirectoryExcluded(Path.GetRelativePath(_root, subdirectory)))
                {
                    pending.Push(subdirectory);
                }
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>Resolves a relative path and throws unless it stays inside the root.</summary>
    private string Contain(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Path must not be empty.", nameof(relativePath));
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new UnauthorizedAccessException(
                $"Absolute paths are not allowed in a workspace read: '{relativePath}'.");
        }

        var candidate = Path.GetFullPath(Path.Combine(_root, relativePath));
        if (!IsContained(candidate))
        {
            throw new UnauthorizedAccessException(
                $"Path escapes the workspace root: '{relativePath}'.");
        }

        return candidate;
    }

    private bool IsContained(string fullPath)
    {
        var resolved = ResolveLinks(fullPath);
        return resolved.Equals(_root, StringComparison.OrdinalIgnoreCase)
            || resolved.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Resolves the final link target so a symlink cannot smuggle reads outside the root.</summary>
    private static string ResolveLinks(string fullPath)
    {
        try
        {
            FileSystemInfo info = File.Exists(fullPath)
                ? new FileInfo(fullPath)
                : new DirectoryInfo(fullPath);

            var target = info.Exists ? info.ResolveLinkTarget(returnFinalTarget: true) : null;
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(target?.FullName ?? fullPath));
        }
        catch (IOException)
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
        }
    }
}
