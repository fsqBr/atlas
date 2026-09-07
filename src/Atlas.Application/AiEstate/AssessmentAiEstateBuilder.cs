using Atlas.Application.Assessments;
using Atlas.Application.Findings;
using Atlas.Domain.Findings;

namespace Atlas.Application.AiEstate;

public sealed record AssessmentAiRule(string RuleId, string Title, Severity MaxSeverity, int Count);

/// <summary>
/// The AI estate of one assessment as the UI shows it: the persisted inventory record, whether the AI
/// scanner ran at all, the open personal-data / secrets findings that co-locate with it, and the AI
/// rules currently open. Derived from persisted findings only — the same facts the report section uses.
/// </summary>
public sealed record AssessmentAiEstate(
    bool Scanned,
    AiEstateRecord? Record,
    int OpenPii,
    int OpenSecrets,
    IReadOnlyList<AssessmentAiRule> AiRules);

public sealed class AssessmentAiEstateBuilder(
    IAssessmentRepository assessments,
    IScanRepository scans,
    IFindingRepository findings,
    IRuleCatalog rules)
{
    public async Task<AssessmentAiEstate?> BuildAsync(Guid assessmentId, string? lang, CancellationToken cancellationToken)
    {
        if (await assessments.GetAsync(assessmentId, cancellationToken) is null)
        {
            return null;
        }

        var scanned = (await scans.ListByAssessmentAsync(assessmentId, cancellationToken)).Any(s => s.ScannerId == AiEstateRuleIds.ScannerId);
        var inventory = await findings.GetLatestOccurrenceDataByRuleAsync([assessmentId], AiEstateRuleIds.Inventory, cancellationToken);
        var record = inventory.TryGetValue(assessmentId, out var json) ? AiEstateRecord.Parse(json) : null;

        var open = await findings.SummarizeOpenAsync([assessmentId], cancellationToken);
        var catalog = await rules.GetAllAsync(cancellationToken);
        var aiRules = open
            .Where(o => AiEstateRuleIds.IsAiRule(o.RuleId) && o.RuleId != AiEstateRuleIds.Inventory)
            .GroupBy(o => o.RuleId, StringComparer.Ordinal)
            .Select(g => new AssessmentAiRule(g.Key, FindingLocalizer.RuleTitle(catalog.GetValueOrDefault(g.Key), g.Key, lang), g.Max(o => o.Severity), g.Sum(o => o.Count)))
            .OrderByDescending(r => r.MaxSeverity)
            .ThenByDescending(r => r.Count)
            .ToList();

        return new AssessmentAiEstate(
            Scanned: scanned || record is not null,
            Record: record,
            OpenPii: open.Where(o => o.RuleId.StartsWith(AiEstateRuleIds.PiiPrefix, StringComparison.Ordinal)).Sum(o => o.Count),
            OpenSecrets: open.Where(o => o.Category == FindingCategory.Secrets && !AiEstateRuleIds.IsAiRule(o.RuleId)).Sum(o => o.Count),
            AiRules: aiRules);
    }
}
