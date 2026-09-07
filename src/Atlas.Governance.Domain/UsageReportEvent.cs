namespace Atlas.Governance.Domain;

/// <summary>
/// One arrival of a usage report: who reported, when, and the day's running totals at that moment. Daily facts
/// carry the truth; these events only make the day visible while it happens (who is active now, tokens per hour,
/// the intraday curve) and are pruned after <see cref="RetentionDays"/>.
/// </summary>
public sealed class UsageReportEvent
{
    public const int RetentionDays = 14;

    private UsageReportEvent()
    {
    }

    public UsageReportEvent(Guid id, Guid tenantId, string actor, string tool, DateOnly period, DateTimeOffset reportedAtUtc, long tokensToday, decimal? estimatedCostToday, int requestsToday)
    {
        Id = id;
        TenantId = tenantId;
        Actor = actor;
        Tool = tool;
        Period = period;
        ReportedAtUtc = reportedAtUtc;
        TokensToday = tokensToday;
        EstimatedCostToday = estimatedCostToday;
        RequestsToday = requestsToday;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Actor { get; private set; } = null!;

    public string Tool { get; private set; } = null!;

    /// <summary>The UTC day the totals belong to (the day the report covered as "today").</summary>
    public DateOnly Period { get; private set; }

    public DateTimeOffset ReportedAtUtc { get; private set; }

    public long TokensToday { get; private set; }

    public decimal? EstimatedCostToday { get; private set; }

    public int RequestsToday { get; private set; }
}
