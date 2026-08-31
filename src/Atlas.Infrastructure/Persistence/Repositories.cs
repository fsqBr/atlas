using Atlas.Application.Assessments;
using Atlas.Application.Audit;
using Atlas.Application.Credentials;
using Atlas.Application.Findings;
using Atlas.Domain.Credentials;
using Atlas.Domain.Assessments;
using Atlas.Domain.Findings;
using Atlas.Domain.Health;
using Atlas.Domain.Rules;
using Atlas.Domain.Scans;
using Atlas.Scanner.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Infrastructure.Persistence;

public sealed class EfUnitOfWork(AtlasDbContext db) : IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

public sealed class AssessmentRepository(AtlasDbContext db) : IAssessmentRepository
{
    public Task<Assessment?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Assessments.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListIdsAsync(CancellationToken cancellationToken) =>
        await db.Assessments.Select(a => a.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Assessment>> ListRecentAsync(int take, CancellationToken cancellationToken) =>
        await db.Assessments
            .OrderByDescending(a => a.CreatedAtUtc)
            .Take(take)
            .ToListAsync(cancellationToken);

    public void Add(Assessment assessment) => db.Assessments.Add(assessment);

    public void Remove(Assessment assessment) => db.Assessments.Remove(assessment);

    public async Task<Assessment?> FindByLocatorAsync(string sourceKind, string locator, string? branch, CancellationToken cancellationToken)
    {
        var key = Assessment.NormalizeRepositoryKey(locator);
        var candidates = await db.Assessments
            .Where(a => a.SourceKind == sourceKind)
            .OrderByDescending(a => a.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        var matching = candidates.Where(a => Assessment.NormalizeRepositoryKey(a.SourceLocator) == key).ToList();
        return matching.FirstOrDefault(a => branch is not null && string.Equals(a.Branch, branch, StringComparison.OrdinalIgnoreCase))
            ?? matching.FirstOrDefault(a => a.Branch is null)
            ?? matching.FirstOrDefault();
    }

    public async Task<IReadOnlyList<string>> ListSourceLocatorsAsync(string sourceKind, CancellationToken cancellationToken) =>
        await db.Assessments.Where(a => a.SourceKind == sourceKind).Select(a => a.SourceLocator).Distinct().ToListAsync(cancellationToken);
}

public sealed class AssessmentRunRepository(AtlasDbContext db) : IAssessmentRunRepository
{
    public void Add(AssessmentRun run) => db.AssessmentRuns.Add(run);

    public async Task<IReadOnlyList<Atlas.Application.Portfolio.CompletedRunPoint>> ListCompletedPointsAsync(CancellationToken cancellationToken)
    {
        // Health snapshots, not run rows: triage and suppression policies recompute snapshots
        // (runId null), so the trend agrees with the portfolio header after a triage session.
        // The Assessments subquery applies the sharing filter (v0.27 ACL): an assessment the caller
        // cannot see contributes nothing to the trend — same visibility as the portfolio itself.
        // The window is bounded to what the trend can ever chart (MaxWeeks plus one week of slack).
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7 * (Atlas.Application.Portfolio.PortfolioTrend.MaxWeeks + 1));
        var rows = await db.HealthSnapshots
            .Where(h => h.CreatedAtUtc >= cutoff && db.Assessments.Any(a => a.Id == h.AssessmentId))
            .Select(h => new { h.AssessmentId, h.CreatedAtUtc, h.Score, h.OpenFindings, h.DimensionsJson })
            .ToListAsync(cancellationToken);
        return rows
            .Select(h => new Atlas.Application.Portfolio.CompletedRunPoint(
                h.AssessmentId, h.CreatedAtUtc, h.Score, h.OpenFindings, ParseDimensionScores(h.DimensionsJson)))
            .ToList();
    }

    private static IReadOnlyDictionary<string, int>? ParseDimensionScores(string dimensionsJson)
    {
        try
        {
            var dimensions = System.Text.Json.JsonSerializer.Deserialize<List<Atlas.Domain.Health.HealthDimension>>(
                dimensionsJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            return dimensions is null or [] ? null : dimensions.ToDictionary(d => d.Name, d => d.Score, StringComparer.Ordinal);
        }
        catch (System.Text.Json.JsonException)
        {
            return null; // an unreadable legacy snapshot only loses its dimension detail, never the point
        }
    }

    public Task<AssessmentRun?> GetAsync(Guid runId, CancellationToken cancellationToken) =>
        db.AssessmentRuns.SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);

    public async Task<IReadOnlyList<AssessmentRun>> ListByAssessmentAsync(Guid assessmentId, CancellationToken cancellationToken) =>
        await db.AssessmentRuns
            .Where(r => r.AssessmentId == assessmentId)
            .OrderByDescending(r => r.Number)
            .ToListAsync(cancellationToken);

    public async Task<int> NextNumberAsync(Guid assessmentId, CancellationToken cancellationToken) =>
        (await db.AssessmentRuns.Where(r => r.AssessmentId == assessmentId).MaxAsync(r => (int?)r.Number, cancellationToken) ?? 0) + 1;
}

public sealed class ScanRepository(AtlasDbContext db) : IScanRepository
{
    public void Add(Scan scan) => db.Scans.Add(scan);

    public async Task<IReadOnlyList<Scan>> ListByRunAsync(Guid runId, CancellationToken cancellationToken) =>
        await db.Scans.Where(s => s.RunId == runId).OrderBy(s => s.StartedAtUtc).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Scan>> ListByAssessmentAsync(Guid assessmentId, CancellationToken cancellationToken) =>
        await db.Scans
            .Where(s => s.AssessmentId == assessmentId)
            .OrderBy(s => s.StartedAtUtc)
            .ToListAsync(cancellationToken);

    public Task<bool> HasSucceededAsync(
        Guid assessmentId, string scannerId, string commitSha, CancellationToken cancellationToken) =>
        db.Scans.AnyAsync(
            s => s.AssessmentId == assessmentId
                 && s.ScannerId == scannerId
                 && s.CommitSha == commitSha
                 && s.Status == ScanStatus.Succeeded,
            cancellationToken);
}

public sealed class SuppressionRepository(AtlasDbContext db) : ISuppressionRepository
{
    public void Add(FindingSuppression suppression) => db.FindingSuppressions.Add(suppression);

    public Task<FindingSuppression?> GetActiveAsync(Guid findingId, CancellationToken cancellationToken) =>
        db.FindingSuppressions
            .Where(s => s.FindingId == findingId && s.RevokedAtUtc == null)
            .OrderByDescending(s => s.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, FindingSuppression>> GetActiveForAsync(
        IReadOnlyCollection<Guid> findingIds, CancellationToken cancellationToken) =>
        (await db.FindingSuppressions
            .Where(s => findingIds.Contains(s.FindingId) && s.RevokedAtUtc == null)
            .ToListAsync(cancellationToken))
        .GroupBy(s => s.FindingId)
        .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.CreatedAtUtc).First());

    public async Task<IReadOnlyList<FindingSuppression>> ListByAssessmentAsync(Guid assessmentId, CancellationToken cancellationToken) =>
        await db.FindingSuppressions.Where(s => s.AssessmentId == assessmentId).OrderByDescending(s => s.CreatedAtUtc).ToListAsync(cancellationToken);
}

public sealed class FindingRepository(AtlasDbContext db) : IFindingRepository
{
    public Task<Finding?> GetAsync(Guid findingId, CancellationToken cancellationToken) =>
        db.Findings.SingleOrDefaultAsync(f => f.Id == findingId, cancellationToken);

    public Task<FindingOccurrence?> GetLatestOccurrenceAsync(Guid findingId, CancellationToken cancellationToken) =>
        db.FindingOccurrences.Where(o => o.FindingId == findingId).OrderByDescending(o => o.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<Finding>> ListExpiredSuppressedAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.Findings
            .Where(f => f.Status == FindingStatus.Suppressed)
            .Where(f => db.FindingSuppressions
                .Where(s => s.FindingId == f.Id && s.RevokedAtUtc == null)
                .OrderByDescending(s => s.CreatedAtUtc)
                .Select(s => s.ExpiresAtUtc)
                .FirstOrDefault() < now)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Atlas.Application.Portfolio.OpenFindingSummary>> SummarizeOpenAsync(
        IReadOnlyCollection<Guid> assessmentIds, CancellationToken cancellationToken) =>
        await db.Findings
            .Where(f => assessmentIds.Contains(f.AssessmentId) && (f.Status == FindingStatus.Open || f.Status == FindingStatus.Regressed))
            .GroupBy(f => new { f.AssessmentId, f.RuleId, f.Category, f.Severity })
            .Select(g => new Atlas.Application.Portfolio.OpenFindingSummary(g.Key.AssessmentId, g.Key.RuleId, g.Key.Category, g.Key.Severity, g.Count()))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Finding>> GetByAssessmentAndRulesAsync(
        Guid assessmentId, IReadOnlyCollection<string> ruleIds, CancellationToken cancellationToken) =>
        await db.Findings
            .Where(f => f.AssessmentId == assessmentId && ruleIds.Contains(f.RuleId))
            .ToListAsync(cancellationToken);

    public void AddRange(IEnumerable<Finding> findings) => db.Findings.AddRange(findings);

    public void AddOccurrences(IEnumerable<FindingOccurrence> occurrences) =>
        db.FindingOccurrences.AddRange(occurrences);

    public async Task<IReadOnlyList<FindingWithLatestOccurrence>> ListTouchedByScansAsync(
        Guid assessmentId, IReadOnlyCollection<Guid> scanIds, CancellationToken cancellationToken)
    {
        var touched = await db.Findings
            .Where(f => f.AssessmentId == assessmentId
                        && (scanIds.Contains(f.FirstSeenScanId)
                            || scanIds.Contains(f.LastSeenScanId)
                            || (f.ResolvedScanId != null && scanIds.Contains(f.ResolvedScanId.Value))))
            .ToListAsync(cancellationToken);

        var ids = touched.Select(f => f.Id).ToList();
        var latest = (await db.FindingOccurrences
                .Where(o => ids.Contains(o.FindingId))
                .OrderByDescending(o => o.CreatedAtUtc)
                .ToListAsync(cancellationToken))
            .GroupBy(o => o.FindingId)
            .ToDictionary(g => g.Key, g => g.First());

        return touched.Select(f => new FindingWithLatestOccurrence(f, latest.GetValueOrDefault(f.Id))).ToList();
    }

    public async Task<IReadOnlyList<Finding>> ListOpenAsync(Guid assessmentId, CancellationToken cancellationToken) =>
        await db.Findings
            .Where(f => f.AssessmentId == assessmentId
                        && (f.Status == FindingStatus.Open || f.Status == FindingStatus.Regressed))
            .ToListAsync(cancellationToken);

    public async Task<Page<FindingWithLatestOccurrence>> ListAsync(
        Guid assessmentId, int skip, int take, CancellationToken cancellationToken, FindingFilter? filter = null)
    {
        filter ??= FindingFilter.None;
        var query = db.Findings.Where(f => f.AssessmentId == assessmentId);

        if (filter.Severity is { } severity)
        {
            query = query.Where(f => f.Severity == severity);
        }

        if (filter.Category is { } category)
        {
            query = query.Where(f => f.Category == category);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(f => f.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(filter.RuleId))
        {
            query = query.Where(f => f.RuleId == filter.RuleId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = $"%{filter.Search.Trim()}%";
            query = query.Where(f => EF.Functions.ILike(f.Title, term) || EF.Functions.ILike(f.RuleId, term));
        }

        var total = await query.CountAsync(cancellationToken);

        var findings = await query
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Status)
            .ThenBy(f => f.CreatedAtUtc)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        var ids = findings.Select(f => f.Id).ToList();
        var latestByFinding = (await db.FindingOccurrences
                .Where(o => ids.Contains(o.FindingId))
                .OrderByDescending(o => o.CreatedAtUtc)
                .ToListAsync(cancellationToken))
            .GroupBy(o => o.FindingId)
            .ToDictionary(g => g.Key, g => g.First());

        var items = findings
            .Select(f => new FindingWithLatestOccurrence(f, latestByFinding.GetValueOrDefault(f.Id)))
            .ToList();

        return new Page<FindingWithLatestOccurrence>(items, total);
    }
}

public sealed class RuleCatalog(AtlasDbContext db) : IRuleCatalog
{
    public async Task<IReadOnlyDictionary<string, RuleDefinition>> UpsertAsync(
        string scannerId, IReadOnlyList<RuleSpec> rules, CancellationToken cancellationToken)
    {
        var ids = rules.Select(r => r.Id).ToList();
        var existing = await db.RuleDefinitions
            .Where(r => ids.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, cancellationToken);

        foreach (var spec in rules)
        {
            var localizations = Atlas.Application.Findings.FindingLocalizer.Serialize(spec.Localizations);
            if (existing.TryGetValue(spec.Id, out var definition))
            {
                definition.Update(spec.Version, spec.Category, spec.DefaultSeverity, spec.Title, spec.Description, spec.Remediation, localizations);
            }
            else
            {
                definition = new RuleDefinition(
                    spec.Id, scannerId, spec.Version, spec.Category, spec.DefaultSeverity,
                    spec.Title, spec.Description, spec.Remediation, localizations);
                db.RuleDefinitions.Add(definition);
                existing[spec.Id] = definition;
            }
        }

        // Rules must exist before findings referencing them are inserted.
        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task<IReadOnlyDictionary<string, RuleDefinition>> GetAllAsync(CancellationToken cancellationToken) =>
        await db.RuleDefinitions.ToDictionaryAsync(r => r.Id, cancellationToken);

    public async Task<IReadOnlyList<string>> ListRuleIdsByScannerAsync(string scannerId, CancellationToken cancellationToken) =>
        await db.RuleDefinitions.Where(r => r.ScannerId == scannerId).Select(r => r.Id).ToListAsync(cancellationToken);
}

public sealed class HealthRepository(AtlasDbContext db) : IHealthRepository
{
    public void Add(HealthSnapshot snapshot) => db.HealthSnapshots.Add(snapshot);

    public Task<HealthSnapshot?> GetLatestAsync(Guid assessmentId, CancellationToken cancellationToken) =>
        db.HealthSnapshots
            .Where(h => h.AssessmentId == assessmentId)
            .OrderByDescending(h => h.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, HealthSnapshot>> GetLatestForAsync(
        IReadOnlyCollection<Guid> assessmentIds, CancellationToken cancellationToken)
    {
        var all = await db.HealthSnapshots
            .Where(h => assessmentIds.Contains(h.AssessmentId))
            .ToListAsync(cancellationToken);

        return all
            .GroupBy(h => h.AssessmentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(h => h.CreatedAtUtc).First());
    }

    public Task<HealthSnapshot?> GetByRunAsync(Guid runId, CancellationToken cancellationToken) =>
        db.HealthSnapshots.Where(h => h.RunId == runId).OrderByDescending(h => h.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
}

public sealed class InventoryRepository(AtlasDbContext db) : IInventoryRepository
{
    public void Add(InventorySnapshot snapshot) => db.InventorySnapshots.Add(snapshot);

    public async Task<IReadOnlyList<InventorySnapshot>> GetByRunAsync(Guid runId, CancellationToken cancellationToken) =>
        await db.InventorySnapshots.Where(s => s.RunId == runId).OrderBy(s => s.LanguageId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<InventorySnapshot>>> GetLatestForAsync(
        IReadOnlyCollection<Guid> assessmentIds, CancellationToken cancellationToken)
    {
        var all = await db.InventorySnapshots
            .Where(s => assessmentIds.Contains(s.AssessmentId))
            .ToListAsync(cancellationToken);

        return all
            .GroupBy(s => s.AssessmentId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<InventorySnapshot>)g.GroupBy(s => s.LanguageId).Select(l => l.OrderByDescending(s => s.CreatedAtUtc).First()).OrderBy(s => s.LanguageId).ToList());
    }

    public async Task<IReadOnlyList<InventorySnapshot>> GetLatestByAssessmentAsync(
        Guid assessmentId, CancellationToken cancellationToken)
    {
        var all = await db.InventorySnapshots
            .Where(s => s.AssessmentId == assessmentId)
            .ToListAsync(cancellationToken);

        return all
            .GroupBy(s => s.LanguageId)
            .Select(g => g.OrderByDescending(s => s.CreatedAtUtc).First())
            .OrderBy(s => s.LanguageId)
            .ToList();
    }
}

public sealed class ConnectorCredentialRepository(AtlasDbContext db) : ICredentialRepository
{
    public Task<ConnectorCredential?> GetByNameAsync(Guid tenantId, string name, CancellationToken cancellationToken) =>
        db.ConnectorCredentials.SingleOrDefaultAsync(c => c.TenantId == tenantId && c.Name == name, cancellationToken);

    public async Task<IReadOnlyList<ConnectorCredential>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await db.ConnectorCredentials.Where(c => c.TenantId == tenantId).OrderBy(c => c.Name).ToListAsync(cancellationToken);

    public Task<int> CountAssessmentsUsingAsync(Guid tenantId, string name, CancellationToken cancellationToken) =>
        db.Assessments.CountAsync(a => a.TenantId == tenantId && a.CredentialName == name, cancellationToken);

    public void Add(ConnectorCredential credential) => db.ConnectorCredentials.Add(credential);

    public void Remove(ConnectorCredential credential) => db.ConnectorCredentials.Remove(credential);
}

public sealed class SuppressionPolicyRepository(AtlasDbContext db) : ISuppressionPolicyRepository
{
    public void Add(SuppressionPolicy policy) => db.SuppressionPolicies.Add(policy);

    public void Remove(SuppressionPolicy policy) => db.SuppressionPolicies.Remove(policy);

    public Task<SuppressionPolicy?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.SuppressionPolicies.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task<IReadOnlyList<SuppressionPolicy>> ListForAssessmentAsync(Guid assessmentId, CancellationToken cancellationToken) =>
        await db.SuppressionPolicies.Where(p => p.AssessmentId == null || p.AssessmentId == assessmentId)
            .OrderBy(p => p.AssessmentId == null ? 0 : 1).ThenBy(p => p.CreatedAtUtc).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SuppressionPolicy>> ListAllAsync(CancellationToken cancellationToken) =>
        await db.SuppressionPolicies.OrderBy(p => p.CreatedAtUtc).ToListAsync(cancellationToken);
}

public sealed class ModernizationActualRepository(AtlasDbContext db) : IModernizationActualRepository
{
    public Task<Atlas.Domain.Modernization.ModernizationActual?> GetAsync(Guid assessmentId, CancellationToken cancellationToken) =>
        db.ModernizationActuals.SingleOrDefaultAsync(a => a.AssessmentId == assessmentId, cancellationToken);

    public async Task<IReadOnlyList<Atlas.Domain.Modernization.ModernizationActual>> ListAllAsync(CancellationToken cancellationToken) =>
        await db.ModernizationActuals.OrderByDescending(a => a.RecordedAtUtc).ToListAsync(cancellationToken);

    public void Add(Atlas.Domain.Modernization.ModernizationActual actual) => db.ModernizationActuals.Add(actual);
}

public sealed class AuditRepository(AtlasDbContext db) : IAuditRepository
{
    public void Add(Atlas.Domain.Audit.AuditEntry entry) => db.AuditEntries.Add(entry);

    public async Task<IReadOnlyList<Atlas.Domain.Audit.AuditEntry>> ListRecentAsync(int take, Guid? assessmentId, CancellationToken cancellationToken)
    {
        IQueryable<Atlas.Domain.Audit.AuditEntry> query = db.AuditEntries;
        if (assessmentId is { } id)
        {
            query = query.Where(a => a.AssessmentId == id);
        }

        return await query.OrderByDescending(a => a.AtUtc).Take(Math.Clamp(take, 1, 1000)).ToListAsync(cancellationToken);
    }
}

public sealed class TenantRepository(AtlasDbContext db) : Atlas.Application.Tenants.ITenantRepository
{
    public async Task<IReadOnlyList<Atlas.Domain.Tenants.Tenant>> ListAsync(CancellationToken cancellationToken) =>
        await db.Tenants.OrderBy(t => t.Name).ToListAsync(cancellationToken);

    public Task<Atlas.Domain.Tenants.Tenant?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Tenants.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);

    public Task<Atlas.Domain.Tenants.Tenant?> GetByExternalKeyAsync(string externalKey, CancellationToken cancellationToken) =>
        db.Tenants.SingleOrDefaultAsync(t => t.ExternalKey == externalKey, cancellationToken);

    public void Add(Atlas.Domain.Tenants.Tenant tenant) => db.Tenants.Add(tenant);
}

public sealed class ApiTokenRepository(AtlasDbContext db) : Atlas.Application.Security.IApiTokenRepository
{
    public async Task<IReadOnlyList<Atlas.Domain.Security.ApiToken>> ListAsync(CancellationToken cancellationToken) =>
        await db.ApiTokens.OrderByDescending(t => t.CreatedAtUtc).ToListAsync(cancellationToken);

    public Task<Atlas.Domain.Security.ApiToken?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.ApiTokens.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);

    public Task<Atlas.Domain.Security.ApiToken?> FindByHashAsync(string hash, CancellationToken cancellationToken) =>
        db.ApiTokens.IgnoreQueryFilters().SingleOrDefaultAsync(t => t.Hash == hash, cancellationToken);

    public void Add(Atlas.Domain.Security.ApiToken token) => db.ApiTokens.Add(token);
}

public sealed class AssessmentAccessRepository(AtlasDbContext db) : IAssessmentAccessRepository
{
    public async Task<IReadOnlyList<AssessmentAccess>> ListAsync(Guid assessmentId, CancellationToken cancellationToken) =>
        await db.AssessmentAccesses.Where(a => a.AssessmentId == assessmentId).ToListAsync(cancellationToken);

    public void Add(AssessmentAccess access) => db.AssessmentAccesses.Add(access);

    public void Remove(AssessmentAccess access) => db.AssessmentAccesses.Remove(access);
}
