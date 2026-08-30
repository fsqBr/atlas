using Atlas.Domain.Credentials;

namespace Atlas.Application.Credentials;

public interface ICredentialRepository
{
    Task<ConnectorCredential?> GetByNameAsync(Guid tenantId, string name, CancellationToken cancellationToken);

    Task<IReadOnlyList<ConnectorCredential>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>How many assessments reference the credential by name (blocks deletion while &gt; 0).</summary>
    Task<int> CountAssessmentsUsingAsync(Guid tenantId, string name, CancellationToken cancellationToken);

    void Add(ConnectorCredential credential);

    void Remove(ConnectorCredential credential);
}

/// <summary>
/// Authenticated encryption for stored secrets under the platform master key
/// (Atlas:Secrets:MasterKeyBase64). The envelope is opaque to callers.
/// </summary>
public interface ISecretCipher
{
    bool IsConfigured { get; }

    byte[] Protect(ReadOnlySpan<byte> plaintext);

    byte[] Unprotect(ReadOnlySpan<byte> envelope);
}

public sealed class CredentialInUseException(string name, int assessments)
    : InvalidOperationException($"Credential '{name}' is used by {assessments} assessment(s) and cannot be deleted.")
{
    public string Name { get; } = name;

    public int Assessments { get; } = assessments;
}

public sealed class SecretStoreNotConfiguredException()
    : InvalidOperationException("The secret store is not configured: set Atlas:Secrets:MasterKeyBase64 (ATLAS_MASTER_KEY, 32 random bytes in base64) on the API and the worker.");
