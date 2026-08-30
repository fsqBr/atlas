namespace Atlas.Domain.Assessments;

public enum TargetStatus
{
    None = 0,
    Met = 1,
    OnTrack = 2,
    AtRisk = 3,
    Missed = 4,
}

/// <summary>
/// A health-score goal with a date ("reach 70 by Q4"). Evaluated from the latest
/// score: met, missed (date passed), at risk (due within 30 days or more than 20
/// points away), otherwise on track. Pure and deterministic.
/// </summary>
public static class Targets
{
    public static readonly TimeSpan AtRiskWindow = TimeSpan.FromDays(30);
    public const int AtRiskGap = 20;

    public static TargetStatus Evaluate(int? score, int? targetScore, DateTimeOffset? targetDate, DateTimeOffset now)
    {
        if (targetScore is null)
        {
            return TargetStatus.None;
        }

        if (score is { } s && s >= targetScore)
        {
            return TargetStatus.Met;
        }

        if (targetDate is { } due)
        {
            if (due < now)
            {
                return TargetStatus.Missed;
            }

            if (due - now <= AtRiskWindow)
            {
                return TargetStatus.AtRisk;
            }
        }

        return score is { } current && targetScore - current > AtRiskGap ? TargetStatus.AtRisk : TargetStatus.OnTrack;
    }
}
