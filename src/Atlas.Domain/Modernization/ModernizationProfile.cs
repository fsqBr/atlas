using Atlas.Domain.Findings;

namespace Atlas.Domain.Modernization;

/// <summary>One open finding reduced to what the modernization engines need.</summary>
public sealed record FindingFact(
    string RuleId,
    Severity Severity,
    FindingCategory Category,
    string? FilePath,
    IReadOnlyDictionary<string, string>? Data);

public sealed record ProjectSummary(string Name, string? TargetFramework, bool IsSdkStyle, string? UiFramework = null);

/// <summary>Estate facts from the inventory snapshots (all languages summed).</summary>
public sealed record EstateFacts(
    long LinesOfCode,
    int Files,
    int Types,
    int Methods,
    int MaxComplexity,
    double AverageComplexity,
    double? SymbolResolutionRate,
    string? Tier,
    IReadOnlyList<ProjectSummary> Projects);

public enum BlockerWeight
{
    Prerequisite,
    High,
    Medium,
}

public sealed record BlockerSummary(string RuleId, BlockerWeight Weight, int Occurrences);

/// <summary>
/// The evidence the modernization, cost and roadmap engines reason about —
/// nothing here is inferred, every number comes from persisted findings and
/// inventory (evidence, not template).
/// </summary>
public sealed record ModernizationProfile(
    long LinesOfCode,
    int Projects,
    int Types,
    int Methods,
    int MaxComplexity,
    double AverageComplexity,
    int LegacyFrameworkProjects,
    int ModernFrameworkProjects,
    int UnknownFrameworkProjects,
    int LegacyProjectFormat,
    IReadOnlyList<BlockerSummary> Blockers,
    int ProjectsWithBlockers,
    int CriticalSecurity,
    int HighSecurity,
    int MediumSecurity,
    int SecretsFound,
    int VulnerablePackages,
    bool HasTests,
    double? CoverageLineRate,
    int ProjectsWithoutTests,
    int ArchitectureCycles,
    int HighFanOut,
    bool HasWebUi,
    bool HasWcfRemotingOrMsmq,
    bool HasEntityFramework6,
    double? SymbolResolutionRate,
    string? Tier,
    IReadOnlyDictionary<string, int>? UiFrameworks = null)
{
    /// <summary>Projects on a UI/hosting framework with no path onto modern .NET (WebForms, MVC 5, Web API 2, WCF, Silverlight, Xamarin.Forms).</summary>
    public int NoUpgradePathProjects => (UiFrameworks ?? new Dictionary<string, int>()).Where(kv => NoUpgradePathFrameworks.Contains(kv.Key)).Sum(kv => kv.Value);

    public int DesktopProjects => (UiFrameworks ?? new Dictionary<string, int>()).Where(kv => kv.Key is "WinForms" or "Wpf").Sum(kv => kv.Value);

    public static readonly IReadOnlySet<string> NoUpgradePathFrameworks = new HashSet<string>(StringComparer.Ordinal) { "WebForms", "AspNetMvc5", "AspNetWebApi2", "Silverlight", "Wcf", "XamarinForms" };

    public int PrerequisiteBlockers => Blockers.Where(b => b.Weight == BlockerWeight.Prerequisite).Sum(b => b.Occurrences);

    public int HighBlockers => Blockers.Where(b => b.Weight == BlockerWeight.High).Sum(b => b.Occurrences);

    public int MediumBlockers => Blockers.Where(b => b.Weight == BlockerWeight.Medium).Sum(b => b.Occurrences);

    public double LegacyShare => Projects == 0 ? 0 : (double)LegacyFrameworkProjects / Projects;

    public double UnknownShare => Projects == 0 ? 0 : (double)UnknownFrameworkProjects / Projects;

    public double BlockedProjectShare => Projects == 0 ? 0 : (double)ProjectsWithBlockers / Projects;

    public bool TestDeficit => !HasTests || (CoverageLineRate is { } rate && rate < 0.3);

    /// <summary>Builds the profile from open findings + inventory. Deterministic; the rule-id conventions are the scanners' public contract.</summary>
    public static ModernizationProfile From(IReadOnlyList<FindingFact> findings, EstateFacts estate)
    {
        var legacy = estate.Projects.Count(p => IsLegacyFramework(p.TargetFramework));
        var unknown = estate.Projects.Count(p => string.IsNullOrWhiteSpace(p.TargetFramework));
        var modern = estate.Projects.Count - legacy - unknown;

        var blockerFindings = findings.Where(f => f.RuleId.StartsWith(RuleIds.MigrationBlockerPrefix, StringComparison.Ordinal)).ToList();
        var blockers = blockerFindings
            .GroupBy(f => f.RuleId)
            .Select(g => new BlockerSummary(g.Key, WeightOf(g.Key), g.Count()))
            .OrderBy(b => b.Weight)
            .ThenBy(b => b.RuleId, StringComparer.Ordinal)
            .ToList();
        var projectsWithBlockers = blockerFindings.Select(f => f.FilePath ?? f.RuleId).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        var security = findings.Where(f => f.Category == FindingCategory.Security).ToList();
        var coverage = findings.FirstOrDefault(f => f.RuleId == RuleIds.CoverageSummary)?.Data;
        double? coverageRate = coverage is not null && coverage.TryGetValue("lineRate", out var rateText)
            && double.TryParse(rateText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rate)
            ? rate
            : null;

        return new ModernizationProfile(
            LinesOfCode: estate.LinesOfCode,
            Projects: estate.Projects.Count,
            Types: estate.Types,
            Methods: estate.Methods,
            MaxComplexity: estate.MaxComplexity,
            AverageComplexity: estate.AverageComplexity,
            LegacyFrameworkProjects: legacy,
            ModernFrameworkProjects: modern,
            UnknownFrameworkProjects: unknown,
            LegacyProjectFormat: estate.Projects.Count(p => !p.IsSdkStyle),
            Blockers: blockers,
            ProjectsWithBlockers: Math.Min(projectsWithBlockers, Math.Max(estate.Projects.Count, projectsWithBlockers)),
            CriticalSecurity: security.Count(f => f.Severity == Severity.Critical),
            HighSecurity: security.Count(f => f.Severity == Severity.High),
            MediumSecurity: security.Count(f => f.Severity == Severity.Medium),
            SecretsFound: findings.Count(f => f.Category == FindingCategory.Secrets),
            VulnerablePackages: findings.Count(f => f.RuleId == RuleIds.VulnerablePackage),
            HasTests: findings.All(f => f.RuleId != RuleIds.NoTests),
            CoverageLineRate: coverageRate,
            ProjectsWithoutTests: findings.Count(f => f.RuleId == RuleIds.ProjectUncovered),
            ArchitectureCycles: findings.Count(f => f.RuleId is RuleIds.ProjectCycle or RuleIds.NamespaceCycle),
            HighFanOut: findings.Count(f => f.RuleId == RuleIds.HighFanOut),
            HasWebUi: blockers.Any(b => b.RuleId.EndsWith("mb-003", StringComparison.Ordinal) || b.RuleId.EndsWith("mb-004", StringComparison.Ordinal))
                || estate.Projects.Any(p => p.UiFramework is "WebForms" or "AspNetMvc5"),
            HasWcfRemotingOrMsmq: blockers.Any(b => b.RuleId.EndsWith("mb-007", StringComparison.Ordinal) || b.RuleId.EndsWith("mb-008", StringComparison.Ordinal)
                || b.RuleId.EndsWith("mb-009", StringComparison.Ordinal) || b.RuleId.EndsWith("mb-010", StringComparison.Ordinal)),
            HasEntityFramework6: blockers.Any(b => b.RuleId.EndsWith("mb-006", StringComparison.Ordinal)),
            SymbolResolutionRate: estate.SymbolResolutionRate,
            Tier: estate.Tier,
            UiFrameworks: estate.Projects.Where(p => p.UiFramework is not null).GroupBy(p => p.UiFramework!).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal));
    }

    public static bool IsLegacyFramework(string? targetFramework)
    {
        if (string.IsNullOrWhiteSpace(targetFramework))
        {
            return false;
        }

        var tfm = targetFramework.Trim().ToLowerInvariant();

        // Legacy csproj: <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>.
        if (tfm.StartsWith('v'))
        {
            return true;
        }

        // Modern monikers carry a dot (net8.0, net10.0) or a family name (netstandard2.0, netcoreapp3.1).
        if (tfm.Contains('.') || tfm.Contains("standard", StringComparison.Ordinal) || tfm.Contains("core", StringComparison.Ordinal))
        {
            return false;
        }

        // .NET Framework SDK-style monikers: net20 … net481.
        return System.Text.RegularExpressions.Regex.IsMatch(tfm, "^net[1-4][0-9]{1,2}$");
    }

    private static BlockerWeight WeightOf(string ruleId)
    {
        var code = ruleId[(ruleId.LastIndexOf('.') + 1)..].ToLowerInvariant();
        return code switch
        {
            "mb-001" or "mb-002" => BlockerWeight.Prerequisite,
            "mb-003" or "mb-007" or "mb-008" or "mb-009" or "mb-010" => BlockerWeight.High,
            _ => BlockerWeight.Medium,
        };
    }

    /// <summary>Rule ids the engines depend on (kept in sync with the scanners; covered by tests).</summary>
    public static class RuleIds
    {
        public const string MigrationBlockerPrefix = "dependency.migration-blocker.";
        public const string VulnerablePackage = "dependency.package.vulnerable";
        public const string CoverageSummary = "quality.coverage.summary";
        public const string NoTests = "quality.tests.none";
        public const string ProjectUncovered = "quality.tests.project-uncovered";
        public const string ProjectCycle = "architecture.cycle.project";
        public const string NamespaceCycle = "architecture.cycle.namespace";
        public const string HighFanOut = "architecture.coupling.high-fan-out";
    }
}
