using System.Text.RegularExpressions;

namespace Atlas.Domain.Workspaces;

/// <summary>
/// Paths that are never the customer's own source: build output, package
/// caches, VCS internals, IDE state. Local (borrowed) workspaces typically
/// contain them; git clones rarely do. Directories are pruned during traversal
/// (a node_modules tree can hold 100k files) and source enumerations filter by
/// path segment. TestResults is kept traversable because coverage reports live there.
/// </summary>
public static partial class WorkspaceFilters
{
    /// <summary>Never descended into.</summary>
    private static readonly HashSet<string> PrunedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", ".nuget", "bin", "obj", "node_modules", "artifacts", "graphify-out",
        // Mobile / JS / other-ecosystem build output that rides along in polyglot repositories.
        ".gradle", "Pods", ".expo", ".next", ".angular", ".dart_tool", "DerivedData", "__pycache__", ".venv", "venv",
        ".terraform", "dist",
    };

    /// <summary>Excluded from source enumerations in addition to the pruned set.</summary>
    private static readonly HashSet<string> NonSourceSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "TestResults",
    };

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+\.\d+(\.\d+)+([-+][A-Za-z0-9.-]+)?$")]
    private static partial Regex NuGetPackageFolderName();

    public static bool ShouldPruneDirectory(string directoryName) => PrunedDirectories.Contains(directoryName);

    /// <summary>
    /// "packages" is ambiguous: a legacy NuGet cache (prune) or a monorepo's packages folder (keep).
    /// A NuGet cache has a repositories.config or children named PackageId.1.2.3.
    /// </summary>
    public static bool IsNuGetPackagesFolder(string fullPath)
    {
        try
        {
            if (File.Exists(Path.Combine(fullPath, "repositories.config")))
            {
                return true;
            }

            return Directory.EnumerateDirectories(fullPath)
                .Select(Path.GetFileName)
                .Take(50)
                .Any(name => name is not null && NuGetPackageFolderName().IsMatch(name));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsBuildOrVendorPath(string relativePath)
    {
        foreach (var segment in relativePath.Split('/', '\\'))
        {
            if (PrunedDirectories.Contains(segment) || NonSourceSegments.Contains(segment))
            {
                return true;
            }
        }

        return false;
    }

    public static IEnumerable<string> SourceFiles(this IArtifactReader workspace, string searchPattern) =>
        workspace.EnumerateFiles(searchPattern).Where(p => !IsBuildOrVendorPath(p));
}
