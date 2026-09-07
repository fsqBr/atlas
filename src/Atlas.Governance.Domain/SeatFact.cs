namespace Atlas.Governance.Domain;

/// <summary>
/// One assistant seat as last observed (GitHub Copilot). The seat holder is a keyed pseudonym
/// (HMAC of the login under the installation key, the design notes): the report can count idle seats and
/// an administrator can re-derive who from the provider console — Atlas itself never stores the login.
/// </summary>
public sealed class SeatFact
{
    public const int IdleAfterDays = 60;

    private SeatFact()
    {
    }

    public SeatFact(Guid id, Guid tenantId, Guid sourceId, string provider, string seatKey, string? plan, DateTimeOffset? lastActivityAtUtc, bool pendingCancellation)
    {
        if (string.IsNullOrWhiteSpace(seatKey))
        {
            throw new ArgumentException("Seat key is required.", nameof(seatKey));
        }

        Id = id;
        TenantId = tenantId;
        SourceId = sourceId;
        Provider = CostProviders.Normalize(provider);
        SeatKey = seatKey;
        Plan = plan;
        LastActivityAtUtc = lastActivityAtUtc;
        PendingCancellation = pendingCancellation;
        CollectedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid SourceId { get; private set; }

    public string Provider { get; private set; } = null!;

    public string SeatKey { get; private set; } = null!;

    public string? Plan { get; private set; }

    public DateTimeOffset? LastActivityAtUtc { get; private set; }

    public bool PendingCancellation { get; private set; }

    public DateTimeOffset CollectedAtUtc { get; private set; }

    /// <summary>No activity in the last <paramref name="days"/> days (or never): a seat that is paid for and not used.</summary>
    public bool IsIdle(DateTimeOffset now, int days = IdleAfterDays) =>
        LastActivityAtUtc is null || LastActivityAtUtc.Value < now.AddDays(-days);
}
