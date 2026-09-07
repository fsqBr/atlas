using Atlas.Contracts.Assessments;

namespace Atlas.Api;

public sealed class LocalRoot
{
    /// <summary>Path inside the container (a read-only bind mount).</summary>
    public string Path { get; set; } = "/sources";

    /// <summary>What the user sees (typically the host folder that was mounted).</summary>
    public string? Label { get; set; }
}

public sealed class LocalSourcesOptions
{
    public const string SectionName = "Atlas:LocalSources";

    public string? Root { get; set; }

    public List<LocalRoot> Roots { get; set; } = [];

    public IReadOnlyList<LocalRoot> EffectiveRoots =>
        Roots.Count > 0
            ? Roots.Where(r => !string.IsNullOrWhiteSpace(r.Path)).GroupBy(r => string.IsNullOrWhiteSpace(r.Label) ? r.Path : r.Label).Select(g => g.First()).ToList() // the same host folder mounted twice (defaults for _2/_3) shows once
            : [new LocalRoot { Path = Root ?? "/sources", Label = Root ?? "/sources" }];
}

/// <summary>
/// A file-dialog for the mounted source roots: lists folders one level at a
/// time, never walks whole trees, and refuses any path outside the roots
/// (local folders are borrowed, read-only input — and only the ones
/// the operator mounted).
/// </summary>
public static class LocalSourcesBrowser
{
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "$RECYCLE.BIN", "System Volume Information",
    };

    public static BrowseResponse Browse(LocalSourcesOptions options, string? path)
    {
        var roots = options.EffectiveRoots
            .Select(r => new LocalRootResponse(r.Path, r.Label ?? r.Path, Directory.Exists(r.Path)))
            .ToList();

        if (string.IsNullOrWhiteSpace(path))
        {
            return new BrowseResponse(roots, null, null, []);
        }

        var full = Contain(options, path) ?? throw new UnauthorizedAccessException("Path is outside the mounted source roots.");
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException(full);
        }

        var root = roots.First(r => IsUnder(full, r.Path));
        var atRoot = Normalize(Path.GetFullPath(root.Path)).TrimEnd('/').Equals(full.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        var parent = atRoot ? null : Normalize(Path.GetDirectoryName(full) ?? root.Path);

        var shallow = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true };
        var entries = new List<LocalFolderResponse>();
        foreach (var dir in Directory.EnumerateDirectories(full, "*", new EnumerationOptions { IgnoreInaccessible = true }))
        {
            var name = Path.GetFileName(dir);
            if (Hidden.Contains(name) || name.StartsWith('.'))
            {
                continue;
            }

            bool dotnet, git, sln;
            try
            {
                dotnet = Directory.EnumerateFiles(dir, "*.csproj", shallow).Any();
                sln = Directory.EnumerateFiles(dir, "*.sln", shallow).Any() || Directory.EnumerateFiles(dir, "*.slnx", shallow).Any();
                git = Directory.Exists(Path.Combine(dir, ".git"));
            }
            catch (IOException)
            {
                dotnet = sln = git = false;
            }
            catch (UnauthorizedAccessException)
            {
                dotnet = sln = git = false;
            }

            entries.Add(new LocalFolderResponse(name, Normalize(dir), dotnet || sln, sln, git));
        }

        return new BrowseResponse(roots, Normalize(full), parent, entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Full path when inside a root, null otherwise (no traversal, no symlink escape via GetFullPath normalization).</summary>
    public static string? Contain(LocalSourcesOptions options, string path)
    {
        string full;
        try
        {
            full = Normalize(Path.GetFullPath(path.Replace('\\', '/')));
        }
        catch (ArgumentException)
        {
            return null;
        }

        return options.EffectiveRoots.Any(r => IsUnder(full, r.Path)) ? full : null;
    }

    private static bool IsUnder(string full, string root)
    {
        var r = Normalize(Path.GetFullPath(root)).TrimEnd('/');
        var f = Normalize(full).TrimEnd('/');
        return f.Equals(r, StringComparison.OrdinalIgnoreCase) || f.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
