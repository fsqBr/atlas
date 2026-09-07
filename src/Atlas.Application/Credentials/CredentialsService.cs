using System.Text;
using Atlas.Application.Assessments;
using Atlas.Connector.Abstractions;
using Atlas.Domain.Credentials;
using Atlas.Application.Tenants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Atlas.Application.Credentials;

public sealed record CredentialSummary(
    string Name,
    string? Username,
    string? Description,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastUsedAtUtc,
    int UsedByAssessments);

/// <summary>
/// Write-only management of connector credentials: create/rotate, list
/// (metadata only), delete (refused while assessments reference the name).
/// </summary>
public sealed class CredentialsService(
    ICredentialRepository credentials,
    ISecretCipher cipher,
    IUnitOfWork unitOfWork,
    ITenantContext tenant,
    ILogger<CredentialsService> logger)
{
    public async Task<CredentialSummary> UpsertAsync(
        string name, string? username, string secret, string? description, CancellationToken cancellationToken)
    {
        if (!cipher.IsConfigured)
        {
            throw new SecretStoreNotConfiguredException();
        }

        name = ConnectorCredential.ValidateName(name);
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("Secret must not be empty.", nameof(secret));
        }

        var envelope = cipher.Protect(Encoding.UTF8.GetBytes(secret.Trim()));
        var existing = await credentials.GetByNameAsync(tenant.Require(), name, cancellationToken);
        if (existing is null)
        {
            existing = new ConnectorCredential(Guid.NewGuid(), tenant.Require(), name, username, description, envelope);
            credentials.Add(existing);
            logger.LogInformation("Credential {Name} created.", name);
        }
        else
        {
            existing.Rotate(username, description, envelope);
            logger.LogInformation("Credential {Name} rotated.", name);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        var used = await credentials.CountAssessmentsUsingAsync(tenant.Require(), name, cancellationToken);
        return ToSummary(existing, used);
    }

    public async Task<IReadOnlyList<CredentialSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var list = await credentials.ListAsync(tenant.Require(), cancellationToken);
        var result = new List<CredentialSummary>(list.Count);
        foreach (var credential in list)
        {
            result.Add(ToSummary(credential, await credentials.CountAssessmentsUsingAsync(tenant.Require(), credential.Name, cancellationToken)));
        }

        return result;
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken)
    {
        var existing = await credentials.GetByNameAsync(tenant.Require(), name, cancellationToken);
        if (existing is null)
        {
            return false;
        }

        var used = await credentials.CountAssessmentsUsingAsync(tenant.Require(), name, cancellationToken);
        if (used > 0)
        {
            throw new CredentialInUseException(name, used);
        }

        credentials.Remove(existing);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Credential {Name} deleted.", name);
        return true;
    }

    public Task<bool> ExistsAsync(string name, CancellationToken cancellationToken) =>
        credentials.GetByNameAsync(tenant.Require(), name, cancellationToken).ContinueWith(t => t.Result is not null, cancellationToken);

    private static CredentialSummary ToSummary(ConnectorCredential c, int used) =>
        new(c.Name, c.Username, c.Description, c.CreatedAtUtc, c.UpdatedAtUtc, c.LastUsedAtUtc, used);
}

/// <summary>
/// ICredentialProvider for singleton connectors: opens its own scope per
/// resolution (repositories are scoped), decrypts, records last use.
/// </summary>
public sealed class ScopedCredentialProvider(IServiceScopeFactory scopes) : ICredentialProvider
{
    public async Task<ConnectorCredentialValue?> ResolveAsync(Atlas.Domain.Sources.SourceReference source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.CredentialName))
        {
            return null;
        }

        var name = source.CredentialName;
        using var scope = scopes.CreateScope();
        var tenantId = source.TenantId ?? scope.ServiceProvider.GetRequiredService<ITenantContext>().Require();
        var repository = scope.ServiceProvider.GetRequiredService<ICredentialRepository>();
        var cipher = scope.ServiceProvider.GetRequiredService<ISecretCipher>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var credential = await repository.GetByNameAsync(tenantId, name, cancellationToken);
        if (credential is null)
        {
            return null;
        }

        if (!cipher.IsConfigured)
        {
            throw new SecretStoreNotConfiguredException();
        }

        var secret = Encoding.UTF8.GetString(cipher.Unprotect(credential.Envelope));
        credential.MarkUsed();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new ConnectorCredentialValue(credential.Username, secret);
    }
}
