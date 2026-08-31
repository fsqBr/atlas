namespace Atlas.Domain.Assessments;

/// <summary>
/// One numbered execution of an assessment ("version"). Scans, inventory and
/// health snapshots hang off a run, so two runs can be compared: what is new,
/// what was resolved, what regressed, and how the score moved.
/// </summary>
public sealed class AssessmentRun
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid AssessmentId { get; private set; }
    public int Number { get; private set; }
    public string? CommitSha { get; private set; }
    public AssessmentRunStatus Status { get; private set; }
    public string? FailureReason { get; private set; }
    public int ScannersRun { get; private set; }
    public int ScannersFailed { get; private set; }
    public int FindingsNew { get; private set; }
    public int FindingsRecurring { get; private set; }
    public int FindingsResolved { get; private set; }
    public int FindingsRegressed { get; private set; }
    public int? OpenFindings { get; private set; }
    public int? HealthScore { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset? FinishedAtUtc { get; private set; }

    private AssessmentRun()
    {
    }

    public AssessmentRun(Guid id, Guid tenantId, Guid assessmentId, int number)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || assessmentId == Guid.Empty)
        {
            throw new ArgumentException("Run, tenant and assessment ids must not be empty.");
        }

        if (number < 1)
        {
            throw new ArgumentException("Run number starts at 1.", nameof(number));
        }

        Id = id;
        TenantId = tenantId;
        AssessmentId = assessmentId;
        Number = number;
        Status = AssessmentRunStatus.Running;
        StartedAtUtc = DateTimeOffset.UtcNow;
    }

    public void SetCommit(string? commitSha) => CommitSha = commitSha;

    public void RecordScan(bool succeeded, int created, int recurring, int resolved, int regressed)
    {
        ScannersRun++;
        if (!succeeded)
        {
            ScannersFailed++;
        }

        FindingsNew += created;
        FindingsRecurring += recurring;
        FindingsResolved += resolved;
        FindingsRegressed += regressed;
    }

    public void Complete(int openFindings, int healthScore)
    {
        EnsureRunning();
        OpenFindings = openFindings;
        HealthScore = healthScore;
        Status = ScannersFailed > 0 ? AssessmentRunStatus.CompletedWithWarnings : AssessmentRunStatus.Completed;
        FinishedAtUtc = DateTimeOffset.UtcNow;
    }

    public void Fail(string reason)
    {
        Status = AssessmentRunStatus.Failed;
        FailureReason = reason;
        FinishedAtUtc = DateTimeOffset.UtcNow;
    }

    private void EnsureRunning()
    {
        if (Status != AssessmentRunStatus.Running)
        {
            throw new InvalidOperationException($"Run {Id} is in state {Status}; expected Running.");
        }
    }
}

public enum AssessmentRunStatus
{
    Running,
    Completed,
    CompletedWithWarnings,
    Failed,
}
