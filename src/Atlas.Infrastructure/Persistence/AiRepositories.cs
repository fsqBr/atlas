using Atlas.Application.Ai;
using Atlas.Domain.Ai;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Infrastructure.Persistence;

public sealed class AiSettingsRepository(AtlasDbContext db) : IAiSettingsRepository
{
    public Task<AiProviderSettings?> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
        db.AiProviderSettings.SingleOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

    public void Add(AiProviderSettings settings) => db.AiProviderSettings.Add(settings);
}

public sealed class BusinessRuleRepository(AtlasDbContext db) : IBusinessRuleRepository
{
    public async Task<IReadOnlyList<BusinessRule>> ListAsync(Guid assessmentId, CancellationToken cancellationToken) =>
        await db.BusinessRules.Where(r => r.AssessmentId == assessmentId)
            .OrderBy(r => r.FilePath).ThenBy(r => r.StartLine).ThenBy(r => r.Name)
            .ToListAsync(cancellationToken);

    public async Task ReplaceAsync(Guid assessmentId, IReadOnlyList<BusinessRule> rules, CancellationToken cancellationToken)
    {
        await db.BusinessRules.Where(r => r.AssessmentId == assessmentId).ExecuteDeleteAsync(cancellationToken);
        db.BusinessRules.AddRange(rules);
    }

    public void AddAnalysis(BusinessRuleAnalysis analysis) => db.BusinessRuleAnalyses.Add(analysis);

    public Task<BusinessRule?> GetAsync(Guid ruleId, CancellationToken cancellationToken) =>
        db.BusinessRules.SingleOrDefaultAsync(r => r.Id == ruleId, cancellationToken);

    public async Task<IReadOnlyList<BusinessRule>> ListRatedAsync(int take, CancellationToken cancellationToken) =>
        await db.BusinessRules.Where(r => r.Rating != null).OrderByDescending(r => r.RatedAtUtc).Take(take).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<BusinessRuleAnalysis>> ListAnalysesAsync(Guid assessmentId, int take, CancellationToken cancellationToken) =>
        await db.BusinessRuleAnalyses.Where(a => a.AssessmentId == assessmentId)
            .OrderByDescending(a => a.StartedAtUtc).Take(Math.Clamp(take, 1, 100))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, int>> CountByAssessmentAsync(IReadOnlyCollection<Guid> assessmentIds, CancellationToken cancellationToken) =>
        await db.BusinessRules.Where(r => assessmentIds.Contains(r.AssessmentId))
            .GroupBy(r => r.AssessmentId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
}

public sealed class AiNarrativeRepository(AtlasDbContext db) : IAiNarrativeRepository
{
    public Task<AiNarrative?> GetAsync(Guid assessmentId, string kind, string key, string lang, CancellationToken cancellationToken) =>
        db.AiNarratives.SingleOrDefaultAsync(n => n.AssessmentId == assessmentId && n.Kind == kind && n.Key == key && n.Lang == lang, cancellationToken);

    public async Task<IReadOnlyList<AiNarrative>> ListAsync(Guid assessmentId, string kind, string lang, CancellationToken cancellationToken) =>
        await db.AiNarratives.Where(n => n.AssessmentId == assessmentId && n.Kind == kind && n.Lang == lang).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AiNarrative>> ListRatedAsync(int take, CancellationToken cancellationToken) =>
        await db.AiNarratives.Where(n => n.Rating != null).OrderByDescending(n => n.RatedAtUtc).Take(take).ToListAsync(cancellationToken);

    public void Add(AiNarrative narrative) => db.AiNarratives.Add(narrative);
}
