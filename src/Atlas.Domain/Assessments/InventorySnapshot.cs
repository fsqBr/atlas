namespace Atlas.Domain.Assessments;

/// <summary>
/// Immutable snapshot of what a language analysis saw in one run (
/// drill-down / §31 "MetricSnapshot"): estate size, project system and the tier
/// actually achieved (honesty — the report says how deep Atlas looked).
/// Project details are stored as JSON; aggregates as columns for cheap queries.
/// </summary>
public sealed class InventorySnapshot
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid AssessmentId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public string? CommitSha { get; private set; }
    public Guid? RunId { get; private set; }
    public string LanguageId { get; private set; } = null!;
    public string TierAchieved { get; private set; } = null!;
    public int FileCount { get; private set; }
    public long TotalLines { get; private set; }
    public int TypeCount { get; private set; }
    public int MethodCount { get; private set; }
    public int MaxCyclomaticComplexity { get; private set; }
    public double AverageCyclomaticComplexity { get; private set; }
    public double? SymbolResolutionRate { get; private set; }
    public int ProjectCount { get; private set; }
    public int SolutionCount { get; private set; }
    public string ProjectsJson { get; private set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private InventorySnapshot()
    {
    }

    public InventorySnapshot(
        Guid id,
        Guid tenantId,
        Guid assessmentId,
        Guid workspaceId,
        string? commitSha,
        string languageId,
        string tierAchieved,
        int fileCount,
        long totalLines,
        int typeCount,
        int methodCount,
        int maxCyclomaticComplexity,
        double averageCyclomaticComplexity,
        double? symbolResolutionRate,
        int projectCount,
        int solutionCount,
        string projectsJson,
        Guid? runId = null)
    {
        if (string.IsNullOrWhiteSpace(languageId))
        {
            throw new ArgumentException("Language id must not be empty.", nameof(languageId));
        }

        Id = id;
        TenantId = tenantId;
        AssessmentId = assessmentId;
        WorkspaceId = workspaceId;
        CommitSha = commitSha;
        RunId = runId;
        LanguageId = languageId;
        TierAchieved = tierAchieved;
        FileCount = fileCount;
        TotalLines = totalLines;
        TypeCount = typeCount;
        MethodCount = methodCount;
        MaxCyclomaticComplexity = maxCyclomaticComplexity;
        AverageCyclomaticComplexity = averageCyclomaticComplexity;
        SymbolResolutionRate = symbolResolutionRate;
        ProjectCount = projectCount;
        SolutionCount = solutionCount;
        ProjectsJson = projectsJson;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }
}
