namespace Atlas.Domain.Health;

/// <summary>
/// Immutable health score snapshot per assessment run (review R5):
/// scoped by commit and model version so trends compare like with like.
/// Dimension drill-down is stored as JSON alongside the headline number.
/// </summary>
public sealed class HealthSnapshot
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid AssessmentId { get; private set; }
    public string? CommitSha { get; private set; }
    public Guid? RunId { get; private set; }
    public string ModelVersion { get; private set; } = null!;
    public int Score { get; private set; }
    public RiskLevel RiskLevel { get; private set; }
    public int OpenFindings { get; private set; }
    public int ProjectCount { get; private set; }
    public string DimensionsJson { get; private set; } = null!;
    public string Explanation { get; private set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private HealthSnapshot()
    {
    }

    public HealthSnapshot(
        Guid id,
        Guid tenantId,
        Guid assessmentId,
        string? commitSha,
        HealthResult result,
        int openFindings,
        int projectCount,
        string dimensionsJson,
        Guid? runId = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        Id = id;
        TenantId = tenantId;
        AssessmentId = assessmentId;
        CommitSha = commitSha;
        RunId = runId;
        ModelVersion = result.ModelVersion;
        Score = result.Score;
        RiskLevel = result.RiskLevel;
        OpenFindings = openFindings;
        ProjectCount = projectCount;
        DimensionsJson = dimensionsJson;
        Explanation = result.Explanation;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }
}
