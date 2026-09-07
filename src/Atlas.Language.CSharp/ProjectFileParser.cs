using System.Xml.Linq;
using Atlas.Domain.Workspaces;
using Atlas.Language.Abstractions;

namespace Atlas.Language.CSharp;

/// <summary>
/// Tier 1.5: csproj, packages.config, Directory.Build.props and
/// Directory.Packages.props read as XML data — never evaluated by MSBuild
///. Covers legacy and SDK-style projects, including properties
/// inherited from the nearest Directory.Build.props and versions supplied by
/// central package management (PackageVersion items).
/// </summary>
public static class ProjectFileParser
{
    public static async Task<IReadOnlyList<ProjectFact>> ParseAllAsync(
        IArtifactReader workspace,
        CancellationToken cancellationToken,
        string projectPattern = "*.csproj")
    {
        var packagesConfigByDir = IndexByDirectory(workspace, "packages.config");
        var aspxDirs = new HashSet<string>(workspace.SourceFiles("*.aspx").Concat(workspace.SourceFiles("*.ascx")).Concat(workspace.SourceFiles("*.master")).Select(p => Normalize(Path.GetDirectoryName(p) ?? string.Empty)), StringComparer.OrdinalIgnoreCase);
        var svcDirs = new HashSet<string>(workspace.SourceFiles("*.svc").Select(p => Normalize(Path.GetDirectoryName(p) ?? string.Empty)), StringComparer.OrdinalIgnoreCase);
        var buildPropsByDir = await LoadXmlByDirectoryAsync(workspace, "Directory.Build.props", cancellationToken);
        var packagesPropsByDir = await LoadXmlByDirectoryAsync(workspace, "Directory.Packages.props", cancellationToken);

        var projects = new List<ProjectFact>();
        foreach (var relativePath in workspace.SourceFiles(projectPattern))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                projects.Add(await ParseProjectAsync(
                    workspace, relativePath, packagesConfigByDir, buildPropsByDir, packagesPropsByDir, aspxDirs, svcDirs, cancellationToken));
            }
            catch (System.Xml.XmlException)
            {
                // A malformed csproj is a fact about the estate, not a crash:
                // record the project with no parseable details.
                projects.Add(new ProjectFact(
                    relativePath,
                    Path.GetFileNameWithoutExtension(relativePath),
                    IsSdkStyle: false,
                    TargetFramework: null,
                    PackageReferences: [],
                    ProjectReferences: [],
                    AssemblyReferences: []));
            }
        }

        return projects;
    }

    private static async Task<ProjectFact> ParseProjectAsync(
        IArtifactReader workspace,
        string relativePath,
        IReadOnlyDictionary<string, string> packagesConfigByDir,
        IReadOnlyDictionary<string, XDocument> buildPropsByDir,
        IReadOnlyDictionary<string, XDocument> packagesPropsByDir,
        HashSet<string> aspxDirs,
        HashSet<string> svcDirs,
        CancellationToken cancellationToken)
    {
        var xml = XDocument.Parse(await workspace.ReadAllTextAsync(relativePath, cancellationToken));
        var ns = xml.Root!.GetDefaultNamespace();
        var isSdkStyle = xml.Root.Attribute("Sdk") is not null;
        var projectDir = Path.GetDirectoryName(relativePath) ?? string.Empty;

        var targetFramework = TargetFrameworkOf(xml, ns);
        if (targetFramework is null && NearestAncestor(buildPropsByDir, projectDir) is { } buildProps)
        {
            targetFramework = TargetFrameworkOf(buildProps, buildProps.Root!.GetDefaultNamespace());
        }

        var centralVersions = NearestAncestor(packagesPropsByDir, projectDir) is { } packagesProps
            ? packagesProps.Descendants(packagesProps.Root!.GetDefaultNamespace() + "PackageVersion")
                .Where(p => p.Attribute("Include") is not null)
                .GroupBy(p => p.Attribute("Include")!.Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Attribute("Version")?.Value, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var packageReferences = xml.Descendants(ns + "PackageReference")
            .Select(p =>
            {
                var id = p.Attribute("Include")?.Value ?? p.Attribute("Update")?.Value ?? "?";
                var version = p.Attribute("Version")?.Value
                    ?? p.Element(ns + "Version")?.Value
                    ?? p.Attribute("VersionOverride")?.Value
                    ?? centralVersions.GetValueOrDefault(id);
                return new PackageReferenceFact(id, version, PackageReferenceOrigin.PackageReference);
            })
            .ToList();

        if (packagesConfigByDir.TryGetValue(projectDir, out var packagesConfigPath))
        {
            var packagesXml = XDocument.Parse(await workspace.ReadAllTextAsync(packagesConfigPath, cancellationToken));
            packageReferences.AddRange(packagesXml.Descendants("package")
                .Select(p => new PackageReferenceFact(
                    Id: p.Attribute("id")?.Value ?? "?",
                    Version: p.Attribute("version")?.Value,
                    Origin: PackageReferenceOrigin.PackagesConfig)));
        }

        var projectReferences = xml.Descendants(ns + "ProjectReference")
            .Select(p => p.Attribute("Include")?.Value ?? "?")
            .ToList();

        var assemblyReferences = xml.Descendants(ns + "Reference")
            .Select(r => (r.Attribute("Include")?.Value ?? "?").Split(',')[0].Trim())
            .ToList();

        var normalizedDir = Normalize(projectDir);
        var hasAspx = aspxDirs.Any(d => d.Equals(normalizedDir, StringComparison.OrdinalIgnoreCase) || (normalizedDir.Length > 0 && d.StartsWith(normalizedDir + "/", StringComparison.OrdinalIgnoreCase)) || (normalizedDir.Length == 0 && !d.Contains('/')));
        var hasSvc = svcDirs.Any(d => d.Equals(normalizedDir, StringComparison.OrdinalIgnoreCase) || (normalizedDir.Length > 0 && d.StartsWith(normalizedDir + "/", StringComparison.OrdinalIgnoreCase)));

        return new ProjectFact(
            relativePath,
            Path.GetFileNameWithoutExtension(relativePath),
            isSdkStyle,
            targetFramework,
            packageReferences,
            projectReferences,
            assemblyReferences,
            UiFrameworkDetector.Detect(xml, packageReferences, assemblyReferences, hasAspx, hasSvc));
    }

    private static string? TargetFrameworkOf(XDocument xml, XNamespace ns) =>
        xml.Descendants(ns + "TargetFramework").FirstOrDefault()?.Value
        ?? xml.Descendants(ns + "TargetFrameworks").FirstOrDefault()?.Value
        ?? xml.Descendants(ns + "TargetFrameworkVersion").FirstOrDefault()?.Value;

    /// <summary>MSBuild picks the nearest Directory.*.props walking up from the project directory.</summary>
    private static XDocument? NearestAncestor(IReadOnlyDictionary<string, XDocument> byDirectory, string projectDir)
    {
        var current = Normalize(projectDir);
        while (true)
        {
            if (byDirectory.TryGetValue(current, out var found))
            {
                return found;
            }

            if (current.Length == 0)
            {
                return null;
            }

            var slash = current.LastIndexOf('/');
            current = slash < 0 ? string.Empty : current[..slash];
        }
    }

    private static async Task<Dictionary<string, XDocument>> LoadXmlByDirectoryAsync(
        IArtifactReader workspace, string fileName, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in workspace.SourceFiles(fileName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                result[Normalize(Path.GetDirectoryName(path) ?? string.Empty)] =
                    XDocument.Parse(await workspace.ReadAllTextAsync(path, cancellationToken));
            }
            catch (System.Xml.XmlException)
            {
                // Unparseable props file: projects simply do not inherit from it.
            }
        }

        return result;
    }

    private static Dictionary<string, string> IndexByDirectory(IArtifactReader workspace, string fileName)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in workspace.SourceFiles(fileName))
        {
            result[Path.GetDirectoryName(path) ?? string.Empty] = path;
        }

        return result;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
}
