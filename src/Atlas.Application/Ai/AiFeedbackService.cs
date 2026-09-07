using Atlas.Application.Assessments;
using Atlas.Application.Tenants;
using Atlas.Domain.Ai;

namespace Atlas.Application.Ai;

public sealed record FeedbackBucket(string Key, int Up, int Down)
{
    public int Total => Up + Down;
    public double? HelpfulShare => Total == 0 ? null : Math.Round((double)Up / Total, 3);
}

public sealed record FeedbackEntry(string Kind, string Model, int Rating, string? Comment, Guid AssessmentId, string? RatedBy, DateTimeOffset RatedAtUtc, string Title);

/// <summary>What the people using the AI features think of them — thumbs up/down per kind and per model, plus the latest comments.</summary>
public sealed record AiFeedbackSummary(int Up, int Down, IReadOnlyList<FeedbackBucket> ByKind, IReadOnlyList<FeedbackBucket> ByModel, IReadOnlyList<FeedbackEntry> Recent)
{
    public const string BusinessRuleKind = "business-rule";

    /// <summary>Pure aggregation over rated narratives and rules; the service supplies the rows.</summary>
    public static AiFeedbackSummary From(IReadOnlyList<AiNarrative> narratives, IReadOnlyList<BusinessRule> rules, int recent = 20)
    {
        var entries = narratives.Where(n => n.Rating is 1 or -1)
            .Select(n => new FeedbackEntry(n.Kind, n.Model, n.Rating!.Value, n.FeedbackComment, n.AssessmentId, n.RatedBy, n.RatedAtUtc ?? n.CreatedAtUtc, TitleFor(n)))
            .Concat(rules.Where(r => r.Rating is 1 or -1)
                .Select(r => new FeedbackEntry(BusinessRuleKind, r.Model, r.Rating!.Value, r.FeedbackComment, r.AssessmentId, r.RatedBy, r.RatedAtUtc ?? r.CreatedAtUtc, r.Name)))
            .ToList();

        static FeedbackBucket Bucket(string key, IEnumerable<FeedbackEntry> group) => new(key, group.Count(e => e.Rating > 0), group.Count(e => e.Rating < 0));
        var byKind = entries.GroupBy(e => e.Kind).Select(g => Bucket(g.Key, g)).OrderByDescending(b => b.Total).ThenBy(b => b.Key, StringComparer.Ordinal).ToList();
        var byModel = entries.GroupBy(e => e.Model).Select(g => Bucket(g.Key, g)).OrderByDescending(b => b.Total).ThenBy(b => b.Key, StringComparer.Ordinal).ToList();
        return new AiFeedbackSummary(entries.Count(e => e.Rating > 0), entries.Count(e => e.Rating < 0), byKind, byModel,
            entries.OrderByDescending(e => e.RatedAtUtc).Take(recent).ToList());
    }

    private static string TitleFor(AiNarrative n) => n.Kind switch
    {
        AiNarrative.Kinds.ExecutiveSummary => "Executive summary",
        AiNarrative.Kinds.MigrationPlan => "Migration plan",
        AiNarrative.Kinds.PrSummary => "PR note",
        AiNarrative.Kinds.FindingFix => "Fix suggestion",
        _ => "Finding explanation",
    };
}

/// <summary>
/// Thumbs up / down on anything the model wrote. The rating lives on the
/// narrative or rule itself (one per artefact, last vote wins) so the quality
/// signal can be read per kind, per model and per assessment without another table.
/// </summary>
public sealed class AiFeedbackService(
    IAiNarrativeRepository narratives,
    IBusinessRuleRepository rules,
    IFindingRepository findings,
    IUnitOfWork unitOfWork,
    ITenantContext tenant)
{
    public static readonly IReadOnlySet<string> RatableKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        AiNarrative.Kinds.FindingExplanation, AiNarrative.Kinds.FindingFix, AiNarrative.Kinds.ExecutiveSummary, AiNarrative.Kinds.MigrationPlan,
    };

    public async Task<AiNarrative> RateNarrativeAsync(Guid assessmentId, string kind, Guid? findingId, string? lang, int rating, string? comment, string? author, CancellationToken cancellationToken)
    {
        if (!RatableKinds.Contains(kind))
        {
            throw new ArgumentException($"kind must be one of {string.Join(", ", RatableKinds)}.", nameof(kind));
        }

        string key;
        if (kind is AiNarrative.Kinds.FindingExplanation or AiNarrative.Kinds.FindingFix)
        {
            if (findingId is null)
            {
                throw new ArgumentException("findingId is required for finding narratives.", nameof(findingId));
            }

            var finding = await findings.GetAsync(findingId.Value, cancellationToken);
            if (finding is null || finding.AssessmentId != assessmentId)
            {
                throw new KeyNotFoundException($"Finding {findingId} not found.");
            }

            key = finding.Fingerprint;
        }
        else
        {
            key = kind == AiNarrative.Kinds.ExecutiveSummary ? "summary" : AiNarrativeService.MigrationPlanKey;
        }

        var narrative = await narratives.GetAsync(assessmentId, kind, key, AiNarrative.NormalizeLang(lang), cancellationToken)
            ?? throw new KeyNotFoundException("There is nothing to rate yet for this item and language.");
        narrative.Rate(rating, comment, Actor(author));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return narrative;
    }

    public async Task<BusinessRule> RateBusinessRuleAsync(Guid assessmentId, Guid ruleId, int rating, string? comment, string? author, CancellationToken cancellationToken)
    {
        var rule = await rules.GetAsync(ruleId, cancellationToken);
        if (rule is null || rule.AssessmentId != assessmentId)
        {
            throw new KeyNotFoundException($"Business rule {ruleId} not found.");
        }

        rule.Rate(rating, comment, Actor(author));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return rule;
    }

    public async Task<AiFeedbackSummary> SummarizeAsync(CancellationToken cancellationToken) =>
        AiFeedbackSummary.From(await narratives.ListRatedAsync(1000, cancellationToken), await rules.ListRatedAsync(1000, cancellationToken));

    private string Actor(string? author) => tenant.SubjectName ?? tenant.Subject ?? (string.IsNullOrWhiteSpace(author) ? "anonymous" : author.Trim());
}
