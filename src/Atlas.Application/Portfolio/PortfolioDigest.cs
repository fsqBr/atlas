using Atlas.Domain.Assessments;

namespace Atlas.Application.Portfolio;

public sealed record DigestAssessment(Guid Id, string Name, int? TargetScore, DateTimeOffset? TargetDate);

public sealed record DigestMover(string Name, int From, int To);

public sealed record PortfolioDigest(
    double? AverageScore,
    int? AverageDelta,
    int OpenFindings,
    int? OpenDelta,
    int Assessed,
    IReadOnlyList<DigestMover> Movers,
    int TargetsAtRisk,
    int TargetsMissed);

/// <summary>
/// The weekly executive pulse, recomputed from persisted health snapshots: average health and open
/// findings now vs seven days ago, the assessments that moved most, and the goals in danger.
/// Pure and deterministic — the worker only formats and posts it.
/// </summary>
public static class PortfolioDigestBuilder
{
    public const int MaxMovers = 5;

    public static PortfolioDigest? Build(
        IReadOnlyList<CompletedRunPoint> points,
        IReadOnlyList<DigestAssessment> assessments,
        DateTimeOffset now)
    {
        var names = assessments.ToDictionary(a => a.Id, a => a.Name);
        var current = Latest(points, now);
        if (current.Count == 0)
        {
            return null;
        }

        var weekAgo = Latest(points, now.AddDays(-7));

        var currentScores = current.Values.Where(p => p.HealthScore is not null).Select(p => (double)p.HealthScore!).ToList();
        var previousScores = weekAgo.Values.Where(p => p.HealthScore is not null).Select(p => (double)p.HealthScore!).ToList();
        var averageScore = currentScores.Count == 0 ? (double?)null : Math.Round(currentScores.Average(), 1);
        var averageDelta = averageScore is null || previousScores.Count == 0
            ? (int?)null
            : (int)Math.Round(averageScore.Value - previousScores.Average());

        var open = current.Values.Sum(p => p.OpenFindings ?? 0);
        var openDelta = weekAgo.Count == 0 ? (int?)null : open - weekAgo.Values.Sum(p => p.OpenFindings ?? 0);

        var movers = current
            .Where(kv => kv.Value.HealthScore is not null
                && weekAgo.TryGetValue(kv.Key, out var before) && before.HealthScore is not null
                && before.HealthScore != kv.Value.HealthScore)
            .Select(kv => new DigestMover(
                names.GetValueOrDefault(kv.Key, kv.Key.ToString("N")[..8]),
                weekAgo[kv.Key].HealthScore!.Value,
                kv.Value.HealthScore!.Value))
            .OrderByDescending(m => Math.Abs(m.To - m.From))
            .Take(MaxMovers)
            .ToList();

        var atRisk = 0;
        var missed = 0;
        foreach (var assessment in assessments)
        {
            var score = current.TryGetValue(assessment.Id, out var point) ? point.HealthScore : null;
            var status = Targets.Evaluate(score, assessment.TargetScore, assessment.TargetDate, now);
            atRisk += status == TargetStatus.AtRisk ? 1 : 0;
            missed += status == TargetStatus.Missed ? 1 : 0;
        }

        return new PortfolioDigest(averageScore, averageDelta, open, openDelta, currentScores.Count, movers, atRisk, missed);
    }

    private static Dictionary<Guid, CompletedRunPoint> Latest(IReadOnlyList<CompletedRunPoint> points, DateTimeOffset cutoff) =>
        points
            .Where(p => p.FinishedAtUtc <= cutoff)
            .GroupBy(p => p.AssessmentId)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.FinishedAtUtc).Last());
}
