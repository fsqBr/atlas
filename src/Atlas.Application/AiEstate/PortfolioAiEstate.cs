using Atlas.Application.Portfolio;
using Atlas.Domain.Assessments;
using Atlas.Domain.Findings;

namespace Atlas.Application.AiEstate;

public sealed record PortfolioAiProvider(string Id, string Name, string Kind, int Assessments, bool? Approved);

public sealed record PortfolioAiRow(
    Guid AssessmentId,
    string Name,
    IReadOnlyList<string> Providers,
    IReadOnlyList<string> Frameworks,
    int McpServers,
    int Unapproved,
    int RetiredModels,
    int SecretsInMcp,
    /// <summary>The repository both talks to an external AI provider and has open PII findings — the correlation no traffic-based tool can see.</summary>
    bool PiiCoLocated,
    bool SecretsCoLocated,
    IReadOnlyList<string>? Tags);

/// <summary>
/// The AI estate across the portfolio: one row per assessment whose latest run produced an AI inventory,
/// folded into provider/framework counts and the correlations that make the section worth reading.
/// Team attribution is only ever by assessment tags: nothing here is per person.
/// </summary>
public sealed record PortfolioAiEstate(
    int AssessmentsWithAi,
    int AssessmentsScanned,
    IReadOnlyList<PortfolioAiProvider> Providers,
    IReadOnlyList<(string Framework, int Count)> Frameworks,
    int McpServers,
    int RemoteMcpServers,
    int SecretsInMcp,
    int RetiredModelReferences,
    bool AllowlistConfigured,
    IReadOnlyList<(string Provider, int Assessments)> Unapproved,
    int AiWithPii,
    int AiWithSecrets,
    IReadOnlyList<PortfolioAiRow> Rows,
    string? CatalogVersion,
    AiCostSummary? Costs = null)
{
    /// <param name="inventoryData">Latest <c>ai.inventory</c> occurrence data per assessment (only assessments the AI scanner ran on).</param>
    /// <param name="open">Open findings per assessment/rule (the portfolio's aggregate query).</param>
    public static PortfolioAiEstate Build(
        IReadOnlyList<Assessment> assessments,
        IReadOnlyDictionary<Guid, string?> inventoryData,
        IReadOnlyList<OpenFindingSummary> open)
    {
        var rows = new List<PortfolioAiRow>();
        var providers = new Dictionary<string, (string Name, string Kind, int Count, bool? Approved)>(StringComparer.Ordinal);
        var frameworks = new Dictionary<string, int>(StringComparer.Ordinal);
        var unapproved = new Dictionary<string, int>(StringComparer.Ordinal);
        int mcp = 0, remote = 0, secrets = 0, retired = 0, withPii = 0, withSecrets = 0;
        var allowlist = false;
        string? catalogVersion = null;

        var openByAssessment = open.GroupBy(o => o.AssessmentId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var assessment in assessments.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!inventoryData.TryGetValue(assessment.Id, out var json))
            {
                continue;
            }

            var record = AiEstateRecord.Parse(json);
            if (record is null || record.Providers.Count == 0 && record.Mcp.Count == 0 && record.Frameworks.Count == 0)
            {
                continue;
            }

            catalogVersion ??= record.CatalogVersion;
            allowlist |= record.AllowlistConfigured;
            foreach (var provider in record.Providers)
            {
                var current = providers.TryGetValue(provider.Id, out var existing) ? existing : (Name: provider.Name, Kind: provider.Kind, Count: 0, Approved: provider.Approved);
                providers[provider.Id] = (Name: provider.Name, Kind: provider.Kind, Count: current.Count + 1, Approved: provider.Approved ?? current.Approved);
            }

            foreach (var framework in record.Frameworks)
            {
                frameworks[framework.Name] = frameworks.GetValueOrDefault(framework.Name) + 1;
            }

            foreach (var id in record.Unapproved)
            {
                unapproved[id] = unapproved.GetValueOrDefault(id) + 1;
            }

            mcp += record.Mcp.Count;
            remote += record.RemoteMcp;
            secrets += record.SecretsInMcp;
            retired += record.RetiredModels.Count;

            var openHere = openByAssessment.GetValueOrDefault(assessment.Id) ?? [];
            var external = record.ExternalProviders.Any();
            var pii = external && openHere.Any(o => o.RuleId.StartsWith(AiEstateRuleIds.PiiPrefix, StringComparison.Ordinal) && o.Count > 0);
            var leakedSecrets = external && openHere.Any(o => o.Category == FindingCategory.Secrets && !AiEstateRuleIds.IsAiRule(o.RuleId) && o.Count > 0);
            withPii += pii ? 1 : 0;
            withSecrets += leakedSecrets ? 1 : 0;

            rows.Add(new PortfolioAiRow(
                assessment.Id, assessment.Name,
                record.Providers.Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                record.Frameworks.Select(f => f.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                record.Mcp.Count, record.Unapproved.Count, record.RetiredModels.Count, record.SecretsInMcp, pii, leakedSecrets, assessment.Tags));
        }

        return new PortfolioAiEstate(
            AssessmentsWithAi: rows.Count,
            AssessmentsScanned: inventoryData.Count,
            Providers: providers
                .Select(kv => new PortfolioAiProvider(kv.Key, kv.Value.Name, kv.Value.Kind, kv.Value.Count, kv.Value.Approved))
                .OrderByDescending(p => p.Assessments).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            Frameworks: frameworks.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => (kv.Key, kv.Value)).ToList(),
            McpServers: mcp,
            RemoteMcpServers: remote,
            SecretsInMcp: secrets,
            RetiredModelReferences: retired,
            AllowlistConfigured: allowlist,
            Unapproved: unapproved.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value)).ToList(),
            AiWithPii: withPii,
            AiWithSecrets: withSecrets,
            Rows: rows,
            CatalogVersion: catalogVersion,
            Costs: null);
    }
}
