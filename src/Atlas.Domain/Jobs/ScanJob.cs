namespace Atlas.Domain.Jobs;

/// <summary>
/// Durable unit of asynchronous work: "run this assessment". Claimed
/// with a lease so a dead worker's job is picked up again; retried a bounded
/// number of times before dead-lettering. Scans never run synchronously over HTTP.
/// </summary>
public sealed class ScanJob
{
    public const int MaxAttempts = 3;

    public static class Kinds
    {
        public const string Scan = "scan";
        public const string BusinessRules = "ai.business-rules";
        public const string FindingFix = "ai.fix";
    }

    public const int MaxPayloadLength = 2000;

    public Guid Id { get; private set; }
    public string Kind { get; private set; } = Kinds.Scan;

    /// <summary>Small JSON the job kind needs (e.g. which finding to patch); null for plain scans.</summary>
    public string? Payload { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid AssessmentId { get; private set; }
    public ScanJobState State { get; private set; }
    public int Attempt { get; private set; }
    public string? LeasedBy { get; private set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset QueuedAtUtc { get; private set; }
    public DateTimeOffset? StartedAtUtc { get; private set; }
    public DateTimeOffset? FinishedAtUtc { get; private set; }

    private ScanJob()
    {
    }

    public ScanJob(Guid id, Guid tenantId, Guid assessmentId, string kind = Kinds.Scan, string? payload = null)
    {
        if (kind is not (Kinds.Scan or Kinds.BusinessRules or Kinds.FindingFix))
        {
            throw new ArgumentException($"Unknown job kind '{kind}'.", nameof(kind));
        }

        if (payload is { Length: > MaxPayloadLength })
        {
            throw new ArgumentException($"Job payload exceeds {MaxPayloadLength} characters.", nameof(payload));
        }

        Kind = kind;
        Payload = payload;
        if (id == Guid.Empty || tenantId == Guid.Empty || assessmentId == Guid.Empty)
        {
            throw new ArgumentException("Job, tenant and assessment ids must not be empty.");
        }

        Id = id;
        TenantId = tenantId;
        AssessmentId = assessmentId;
        State = ScanJobState.Queued;
        QueuedAtUtc = DateTimeOffset.UtcNow;
    }

    public bool IsClaimable(DateTimeOffset now) =>
        State == ScanJobState.Queued
        || (State is ScanJobState.Leased or ScanJobState.Running && LeaseExpiresAtUtc is { } expiry && expiry < now);

    public void Claim(string worker, TimeSpan leaseDuration)
    {
        if (!IsClaimable(DateTimeOffset.UtcNow))
        {
            throw new InvalidOperationException($"Job {Id} is not claimable in state {State}.");
        }

        Attempt++;
        LeasedBy = worker;
        LeaseExpiresAtUtc = DateTimeOffset.UtcNow.Add(leaseDuration);
        State = ScanJobState.Leased;
        Error = null;
    }

    public void Start()
    {
        if (State != ScanJobState.Leased)
        {
            throw new InvalidOperationException($"Job {Id} cannot start from state {State}.");
        }

        State = ScanJobState.Running;
        StartedAtUtc = DateTimeOffset.UtcNow;
    }

    public void Heartbeat(TimeSpan leaseDuration)
    {
        if (State is not (ScanJobState.Leased or ScanJobState.Running))
        {
            throw new InvalidOperationException($"Job {Id} has no lease to renew in state {State}.");
        }

        LeaseExpiresAtUtc = DateTimeOffset.UtcNow.Add(leaseDuration);
    }

    public void Succeed()
    {
        EnsureRunning();
        State = ScanJobState.Succeeded;
        FinishedAtUtc = DateTimeOffset.UtcNow;
        ClearLease();
    }

    /// <summary>Requeues while attempts remain; dead-letters otherwise. Never loses the error.</summary>
    public void Fail(string error)
    {
        EnsureRunning();
        Error = error;
        FinishedAtUtc = DateTimeOffset.UtcNow;
        State = Attempt < MaxAttempts ? ScanJobState.Queued : ScanJobState.DeadLetter;
        ClearLease();
    }

    private void ClearLease()
    {
        LeasedBy = null;
        LeaseExpiresAtUtc = null;
    }

    private void EnsureRunning()
    {
        if (State != ScanJobState.Running)
        {
            throw new InvalidOperationException($"Job {Id} is in state {State}; expected Running.");
        }
    }
}

public enum ScanJobState
{
    Queued,
    Leased,
    Running,
    Succeeded,
    DeadLetter,
}
