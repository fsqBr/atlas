using Atlas.Connector.Abstractions;
using Atlas.Domain.Sources;

namespace Atlas.Connector.Git;

/// <summary>
/// Shallow-clone primitive shared by provider connectors (GitHub, Azure DevOps…):
/// they translate their locators into a plain git SourceReference and delegate
/// the actual clone — credentials, isolation and timeouts — to one implementation.
/// </summary>
public interface IGitCloner
{
    Task<MaterializedSource> CloneAsync(SourceReference gitSource, string targetDirectory, CancellationToken cancellationToken);
}
