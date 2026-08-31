using Atlas.Application.Assessments;
using Atlas.Domain.Findings;

namespace Atlas.Reporting;

/// <summary>Render-ready model of the executive report. Pure data; no HTML here.</summary>
public sealed record ExecutiveReport(
    ReportHeader Header,
    IReadOnlyList<ReportScan> Scans,
    IReadOnlyList<ReportInventory> Inventory,
    ReportTotals Totals,
    IReadOnlyList<ReportRuleGroup> RuleGroups,
    IReadOnlyList<ReportFinding> Findings,
    ReportHealth? Health,
    ReportModernization? Modernization = null,
    ReportComparison? Comparison = null,
    IReadOnlyList<ReportProjectRow>? Projects = null,
    string? Verdict = null,
    IReadOnlyList<ReportBusinessRule>? BusinessRules = null,
    ReportAiSummary? AiSummary = null,
    ReportAiSummary? MigrationPlan = null);

/// <summary>A business rule the model recovered; shown in its own section, labelled as AI output.</summary>
public sealed record ReportBusinessRule(string FilePath, string Symbol, int StartLine, string Name, string Description, string Category, IReadOnlyList<string> Conditions, double Confidence, string Model);

/// <summary>Text the model wrote from report facts — the executive summary (page one) or the migration plan draft (Markdown); always labelled.</summary>
public sealed record ReportAiSummary(string Text, string Model, DateTimeOffset CreatedAtUtc);

/// <summary>What changed since the previous run (text already localized).</summary>
public sealed record ReportComparison(int CurrentRun, int PreviousRun, int? HealthDelta, int Resolved, int New, int Regressed, IReadOnlyList<string> TopResolved, IReadOnlyList<string> TopNew);

/// <summary>Open findings per project (longest folder prefix match).</summary>
public sealed record ReportProjectRow(string Project, string? TargetFramework, int Open, int Critical, int High, int Medium, int Low, string? UiFramework = null);

/// <summary>Strategy comparison, cost ranges and roadmap — text already localized.</summary>
public sealed record ReportModernization(
    string RecommendedName,
    string RecommendedDescription,
    IReadOnlyList<ReportStrategy> Strategies,
    ReportEstimate RecommendedEstimate,
    IReadOnlyList<ReportPhase> Phases,
    string ModelVersions);

public sealed record ReportStrategy(string Name, int FitScore, string Risk, bool Recommended, double LikelyHours, double LikelyMonths, decimal LikelyCost, string Currency, IReadOnlyList<string> Rationale, IReadOnlyList<string> Blockers);

public sealed record ReportEstimate(
    double OptimisticHours, double LikelyHours, double ConservativeHours,
    double OptimisticMonths, double LikelyMonths, double ConservativeMonths,
    decimal OptimisticCost, decimal LikelyCost, decimal ConservativeCost, string Currency,
    string Confidence,
    IReadOnlyList<(string Label, double Hours, double Quantity)> Breakdown,
    IReadOnlyList<(string Label, string Value)> Assumptions);

public sealed record ReportPhase(string Name, double Share, double LikelyHours, double LikelyMonths, IReadOnlyList<string> DependsOn, IReadOnlyList<(string Label, int Quantity)> WorkItems);

/// <summary>Headline score with its drill-down (never a mysterious number).</summary>
public sealed record ReportHealth(
    int Score,
    string RiskLevel,
    string ModelVersion,
    string Explanation,
    IReadOnlyList<ReportHealthDimension> Dimensions);

public sealed record ReportHealthDimension(
    string Name,
    double Weight,
    int Score,
    double Penalty,
    IReadOnlyList<string> Contributors);

public sealed record ReportHeader(
    string BrandName,
    string? PreparedBy,
    string AssessmentName,
    string SourceKind,
    string SourceLocator,
    string? Branch,
    string? CommitSha,
    string Status,
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public sealed record ReportScan(
    string ScannerId,
    string Version,
    string Status,
    int Emitted,
    int New,
    int Recurring,
    int Resolved,
    int Regressed,
    string? Error);

public sealed record ReportInventory(
    string LanguageId,
    string Tier,
    int Files,
    long Lines,
    int Types,
    int Methods,
    int MaxComplexity,
    double AverageComplexity,
    double? SymbolResolutionRate,
    int Solutions,
    IReadOnlyList<InventoryProjectEntry> Projects);

public sealed record ReportTotals(
    int Open,
    int Resolved,
    int Suppressed,
    IReadOnlyDictionary<Severity, int> OpenBySeverity,
    IReadOnlyDictionary<FindingCategory, int> OpenByCategory);

/// <summary>One rule across the estate: how bad, how many, where (sample).</summary>
public sealed record ReportRuleGroup(
    string RuleId,
    string Title,
    FindingCategory Category,
    Severity MaxSeverity,
    int OpenCount,
    string? Remediation,
    IReadOnlyList<string> SampleLocations);

public sealed record ReportFinding(
    string RuleId,
    string Title,
    FindingCategory Category,
    Severity Severity,
    FindingStatus Status,
    string? Confidence,
    string? FilePath,
    int? Line,
    string? Symbol,
    string? Message);

public sealed class ReportOptions
{
    public const string SectionName = "Atlas:Report";

    /// <summary>White-label brand shown in the header (the consultancy delivering the assessment).</summary>
    public string BrandName { get; set; } = "Atlas";

    public string? PreparedBy { get; set; }

    /// <summary>Chromium-based browser used for PDF export on machines without the PDF service; auto-detected on PATH when null.</summary>
    public string? ChromiumPath { get; set; }

    /// <summary>Base URL of the Gotenberg PDF sidecar (Docker: http://atlas-pdf:3000). When set, it is used instead of a local browser.</summary>
    public string? PdfServiceUrl { get; set; }

    /// <summary>White-label logo as a data: URI (PNG/SVG); external URLs are not fetched by the PDF sidecar.</summary>
    public string? LogoDataUri { get; set; }

    /// <summary>Accent colour (CSS hex, e.g. #1F6E68) for headings and bars.</summary>
    public string? AccentColor { get; set; }
}
