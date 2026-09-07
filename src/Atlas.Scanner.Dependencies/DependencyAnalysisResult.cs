namespace Atlas.Scanner.Dependencies;

/// <summary>
/// Deterministic dependency facts derived from language-neutral ProjectFacts
///. Every judgement (support status, blocker, vulnerability) cites the
/// versioned catalog it came from so a report can be reproduced later.
/// </summary>
public sealed record DependencyAnalysisResult(
    IReadOnlyList<PackageUsage> Packages,
    ProjectGraph ProjectGraph,
    IReadOnlyList<ProjectFramework> Frameworks,
    IReadOnlyList<MigrationBlocker> MigrationBlockers,
    IReadOnlyList<VulnerablePackage> Vulnerabilities,
    CatalogVersions Catalogs,
    IReadOnlyList<NpmPackage>? NpmPackages = null);

/// <summary>One NuGet package across the estate; multiple versions = a version conflict.</summary>
public sealed record PackageUsage(
    string Id,
    IReadOnlyList<string> Versions,
    IReadOnlyList<string> Projects,
    bool HasVersionConflict,
    bool FromPackagesConfig);

public sealed record ProjectGraph(IReadOnlyList<ProjectNode> Nodes, IReadOnlyList<ProjectEdge> Edges);

/// <summary>FanIn = how many projects depend on this one (centrality input for the risk engine).</summary>
public sealed record ProjectNode(string Path, string Name, int FanIn, int FanOut);

public sealed record ProjectEdge(string From, string To, bool Resolved);

public sealed record ProjectFramework(
    string ProjectPath,
    string RawMoniker,
    string Framework,
    string? Version,
    FrameworkSupportStatus Status,
    DateOnly? EndOfLife,
    string Explanation);

public enum FrameworkSupportStatus
{
    Supported,

    /// <summary>Still serviced but not evolving (e.g. .NET Framework 4.7+): fine to run, a modernization signal.</summary>
    SupportedLegacy,

    /// <summary>Supported today, end of life within the planning horizon.</summary>
    EndingSoon,
    EndOfLife,
    Unknown,
}

public sealed record MigrationBlocker(
    string RuleId,
    string ProjectPath,
    BlockerImpact Impact,
    string Title,
    string Evidence,
    string Remediation,
    BlockerEvidence StructuredEvidence);

public enum BlockerImpact
{
    /// <summary>Must happen before anything else (mechanical, tool-assisted).</summary>
    Prerequisite,

    /// <summary>A supported path exists; effort is real but bounded.</summary>
    Medium,

    /// <summary>No direct equivalent on modern .NET; requires redesign or replacement.</summary>
    High,
}

public sealed record VulnerablePackage(
    string PackageId,
    string Version,
    string VulnerabilityId,
    string? Summary,
    string? Severity,
    string? FixedVersion,
    IReadOnlyList<string> Projects,
    IReadOnlyList<string> Aliases,
    string Ecosystem = "NuGet");

public sealed record CatalogVersions(string FrameworkSupport, string MigrationRules, string? VulnerabilityBundle);
