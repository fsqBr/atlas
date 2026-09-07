using Atlas.Domain.Workspaces;
using Atlas.Language.Abstractions;

namespace Atlas.Application.Ai;

/// <summary>
/// Merges candidates from every language (C#, SQL, …) into one ranked list. Each
/// source gets the full budget so a SQL-heavy estate is not starved by C# noise;
/// the merge then keeps the best <c>max</c> overall.
/// </summary>
public sealed class CompositeBusinessRuleCandidateSource(IEnumerable<IBusinessRuleCandidateSource> sources) : IBusinessRuleCandidateSource
{
    private readonly IReadOnlyList<IBusinessRuleCandidateSource> _sources = sources.ToList();

    public async Task<IReadOnlyList<BusinessRuleCandidate>> FindAsync(IArtifactReader workspace, int max, CancellationToken cancellationToken)
    {
        var all = new List<BusinessRuleCandidate>();
        foreach (var source in _sources)
        {
            all.AddRange(await source.FindAsync(workspace, max, cancellationToken));
        }

        return all.OrderByDescending(c => c.Score).ThenBy(c => c.FilePath, StringComparer.Ordinal).ThenBy(c => c.StartLine).Take(Math.Max(1, max)).ToList();
    }
}
