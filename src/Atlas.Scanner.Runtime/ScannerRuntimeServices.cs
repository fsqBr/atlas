using Atlas.Domain.Workspaces;
using Atlas.Scanner.Abstractions;

namespace Atlas.Scanner.Runtime;

public sealed class ContainedArtifactReaderFactory : IArtifactReaderFactory
{
    public IArtifactReader Create(string rootPath) => new ContainedArtifactReader(rootPath);

    public IArtifactReader Create(string rootPath, IReadOnlyList<string> excludeGlobs) => new ContainedArtifactReader(rootPath, excludeGlobs);
}

/// <summary>Collects candidates in memory for the reconciler; one instance per scan.</summary>
public sealed class InMemoryFindingSink : IFindingSink
{
    private readonly List<FindingCandidate> _candidates = [];

    public IReadOnlyList<FindingCandidate> Candidates => _candidates;

    public void Emit(FindingCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        _candidates.Add(candidate);
    }
}
