using Atlas.Language.Abstractions;
using Atlas.Scanner.Dependencies.Vulnerabilities;

namespace Atlas.Scanner.Dependencies;

/// <summary>
/// Turns language-neutral ProjectFacts into dependency intelligence: package
/// inventory and version conflicts, project graph with centrality, framework
/// support status, migration blockers and known vulnerabilities. Pure function
/// of its inputs plus versioned catalogs — no network, no execution.
/// </summary>
public sealed class DependencyAnalyzer(IVulnerabilitySource vulnerabilities)
{
    public Task<DependencyAnalysisResult> AnalyzeAsync(
        IReadOnlyList<ProjectFact> projects,
        DateOnly today,
        CancellationToken cancellationToken) => AnalyzeAsync(projects, [], today, cancellationToken);

    public async Task<DependencyAnalysisResult> AnalyzeAsync(
        IReadOnlyList<ProjectFact> projects,
        IReadOnlyList<NpmPackage> npmPackages,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var packages = BuildPackageInventory(projects);
        var graph = BuildProjectGraph(projects);

        var frameworks = projects
            .SelectMany(p => FrameworkSupportCatalog.Evaluate(p.RelativePath, p.TargetFramework, today))
            .ToList();

        var blockers = projects
            .SelectMany(MigrationBlockerRules.Evaluate)
            .ToList();

        var vulnerable = await FindVulnerabilitiesAsync(projects, packages, cancellationToken);
        vulnerable.AddRange(await FindNpmVulnerabilitiesAsync(npmPackages, cancellationToken));

        return new DependencyAnalysisResult(
            packages,
            graph,
            frameworks,
            blockers,
            vulnerable,
            new CatalogVersions(
                FrameworkSupportCatalog.Version,
                MigrationBlockerRules.Version,
                vulnerabilities.BundleVersion),
            npmPackages);
    }

    private async Task<List<VulnerablePackage>> FindNpmVulnerabilitiesAsync(IReadOnlyList<NpmPackage> npmPackages, CancellationToken cancellationToken)
    {
        var results = new List<VulnerablePackage>();
        foreach (var group in npmPackages.GroupBy(p => (p.Name.ToLowerInvariant(), p.Version)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = group.First();
            var matches = await vulnerabilities.FindAsync("npm", first.Name, first.Version, cancellationToken);
            if (matches.Count == 0)
            {
                continue;
            }

            var lockfiles = group.Select(p => p.LockfilePath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal).ToList();
            results.AddRange(matches.Select(m => new VulnerablePackage(
                first.Name, first.Version, m.Id, m.Summary, m.Severity, m.FixedVersion, lockfiles, m.Aliases, "npm")));
        }

        return results;
    }

    private static List<PackageUsage> BuildPackageInventory(IReadOnlyList<ProjectFact> projects) =>
        projects
            .SelectMany(p => p.PackageReferences.Select(r => (Project: p.RelativePath, Ref: r)))
            .GroupBy(x => x.Ref.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var versions = g.Select(x => x.Ref.Version)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new PackageUsage(
                    Id: g.Key,
                    Versions: versions,
                    Projects: g.Select(x => x.Project).Distinct().OrderBy(p => p).ToList(),
                    HasVersionConflict: versions.Count > 1,
                    FromPackagesConfig: g.Any(x => x.Ref.Origin == PackageReferenceOrigin.PackagesConfig));
            })
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static ProjectGraph BuildProjectGraph(IReadOnlyList<ProjectFact> projects)
    {
        var byNormalizedPath = projects.ToDictionary(
            p => Normalize(p.RelativePath), p => p.RelativePath, StringComparer.OrdinalIgnoreCase);

        var edges = new List<ProjectEdge>();
        foreach (var project in projects)
        {
            var projectDir = Path.GetDirectoryName(project.RelativePath) ?? string.Empty;
            foreach (var reference in project.ProjectReferences)
            {
                var combined = Path.Combine(projectDir, reference.Replace('\\', Path.DirectorySeparatorChar));
                var normalized = Normalize(Path.GetRelativePath(".", combined));

                edges.Add(byNormalizedPath.TryGetValue(normalized, out var target)
                    ? new ProjectEdge(project.RelativePath, target, Resolved: true)
                    : new ProjectEdge(project.RelativePath, normalized, Resolved: false));
            }
        }

        var nodes = projects
            .Select(p => new ProjectNode(
                p.RelativePath,
                p.Name,
                FanIn: edges.Count(e => e.Resolved && e.To == p.RelativePath),
                FanOut: edges.Count(e => e.From == p.RelativePath)))
            .ToList();

        return new ProjectGraph(nodes, edges);
    }

    private async Task<List<VulnerablePackage>> FindVulnerabilitiesAsync(
        IReadOnlyList<ProjectFact> projects,
        IReadOnlyList<PackageUsage> packages,
        CancellationToken cancellationToken)
    {
        var results = new List<VulnerablePackage>();

        foreach (var package in packages)
        {
            foreach (var version in package.Versions)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var matches = await vulnerabilities.FindAsync(package.Id, version, cancellationToken);
                if (matches.Count == 0)
                {
                    continue;
                }

                var usingProjects = projects
                    .Where(p => p.PackageReferences.Any(r =>
                        r.Id.Equals(package.Id, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase)))
                    .Select(p => p.RelativePath)
                    .OrderBy(p => p)
                    .ToList();

                results.AddRange(matches.Select(m => new VulnerablePackage(
                    package.Id, version, m.Id, m.Summary, m.Severity, m.FixedVersion, usingProjects, m.Aliases)));
            }
        }

        return results;
    }

    private static string Normalize(string path) =>
        path.Replace('\\', '/').TrimStart('.', '/');
}
