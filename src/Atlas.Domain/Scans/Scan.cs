namespace Atlas.Domain.Scans;

/// <summary>
/// One execution of one scanner against one workspace. Scans never mutate
/// findings directly; the reconciler does, and records its counts here.
/// </summary>
public sealed class Scan
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid AssessmentId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid? RunId { get; private set; }
    public string ScannerId { get; private set; } = null!;
    public string ScannerVersion { get; private set; } = null!;
    public string? CommitSha { get; private set; }
    public ScanStatus Status { get; private set; }
    public string? Error { get; private set; }
    public int FindingsEmitted { get; private set; }
    public int FindingsNew { get; private set; }
    public int FindingsRecurring { get; private set; }
    public int FindingsResolved { get; private set; }
    public int FindingsRegressed { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset? FinishedAtUtc { get; private set; }

    private Scan()
    {
    }

    public static Scan Start(
        Guid id,
        Guid tenantId,
        Guid assessmentId,
        Guid workspaceId,
        string scannerId,
        string scannerVersion,
        string? commitSha,
        Guid? runId = null)
    {
        if (string.IsNullOrWhiteSpace(scannerId))
        {
            throw new ArgumentException("Scanner id must not be empty.", nameof(scannerId));
        }

        return new Scan
        {
            Id = id,
            TenantId = tenantId,
            AssessmentId = assessmentId,
            WorkspaceId = workspaceId,
            RunId = runId,
            ScannerId = scannerId,
            ScannerVersion = scannerVersion,
            CommitSha = commitSha,
            Status = ScanStatus.Running,
            StartedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    public void Succeed(int emitted, int created, int recurring, int resolved, int regressed)
    {
        EnsureRunning();
        FindingsEmitted = emitted;
        FindingsNew = created;
        FindingsRecurring = recurring;
        FindingsResolved = resolved;
        FindingsRegressed = regressed;
        Status = ScanStatus.Succeeded;
        FinishedAtUtc = DateTimeOffset.UtcNow;
    }

    public void Fail(string error)
    {
        EnsureRunning();
        Error = error;
        Status = ScanStatus.Failed;
        FinishedAtUtc = DateTimeOffset.UtcNow;
    }

    public void Cancel()
    {
        EnsureRunning();
        Status = ScanStatus.Cancelled;
        FinishedAtUtc = DateTimeOffset.UtcNow;
    }

    private void EnsureRunning()
    {
        if (Status != ScanStatus.Running)
        {
            throw new InvalidOperationException($"Scan {Id} is in state {Status}; expected Running.");
        }
    }
}

public enum ScanStatus
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
}
