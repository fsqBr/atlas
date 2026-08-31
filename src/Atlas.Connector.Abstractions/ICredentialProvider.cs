namespace Atlas.Connector.Abstractions;

/// <summary>Decrypted credential handed to a connector for one materialization. Never persisted or logged.</summary>
public sealed record ConnectorCredentialValue(string? Username, string Secret);

/// <summary>
/// Resolves a credential name (from SourceReference.CredentialName) into its
/// value. Implemented by the application layer over the encrypted secret store;
/// connectors only ever see the value for the duration of a clone.
/// </summary>
public interface ICredentialProvider
{
    /// <summary>Resolves the credential named by <c>source.CredentialName</c> within the source's tenant (or the caller's tenant when unset).</summary>
    Task<ConnectorCredentialValue?> ResolveAsync(Atlas.Domain.Sources.SourceReference source, CancellationToken cancellationToken);
}
