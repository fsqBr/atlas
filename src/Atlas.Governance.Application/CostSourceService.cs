using Atlas.Application.Credentials;
using Atlas.Application.Tenants;
using Atlas.Governance.Domain;
using Microsoft.Extensions.Logging;

namespace Atlas.Governance.Application;

/// <summary>
/// Manages the tenant's cost sources: which providers are connected, through which stored credential,
/// with which scope. Admin-only at the API; the secret never passes through here — only its name.
/// </summary>
public sealed class CostSourceService(
    ICostSourceRepository sources,
    ICredentialRepository credentials,
    IGovernanceUnitOfWork unitOfWork,
    ITenantContext tenant,
    ILogger<CostSourceService> logger)
{
    public Task<IReadOnlyList<CostSource>> ListAsync(CancellationToken cancellationToken) => sources.ListAsync(cancellationToken);

    public async Task<CostSource> UpsertAsync(string provider, string credentialName, string? scope, bool enabled, CancellationToken cancellationToken)
    {
        if (!CostProviders.IsKnown(provider))
        {
            throw new ArgumentException($"Unknown cost provider '{provider}'. Known: {string.Join(", ", CostProviders.All)}.", nameof(provider));
        }

        var normalized = CostProviders.Normalize(provider);
        if (string.IsNullOrWhiteSpace(credentialName))
        {
            throw new ArgumentException("credentialName is required: store the admin key under Credentials first, then reference it by name.", nameof(credentialName));
        }

        if (await credentials.GetByNameAsync(tenant.Require(), credentialName.Trim(), cancellationToken) is null)
        {
            throw new ArgumentException($"No credential named '{credentialName.Trim()}' exists; store the provider admin key under Credentials first.", nameof(credentialName));
        }

        if (normalized == CostProviders.GitHubCopilot && string.IsNullOrWhiteSpace(scope))
        {
            throw new ArgumentException("scope is required for github-copilot: the GitHub organization login.", nameof(scope));
        }

        var existing = await sources.GetAsync(normalized, cancellationToken);
        if (existing is null)
        {
            existing = new CostSource(Guid.NewGuid(), tenant.Require(), normalized, credentialName, scope);
            if (!enabled)
            {
                existing.Update(credentialName, scope, enabled: false);
            }

            sources.Add(existing);
            logger.LogInformation("Cost source {Provider} created (credential {Credential}).", normalized, existing.CredentialName);
        }
        else
        {
            existing.Update(credentialName, scope, enabled);
            logger.LogInformation("Cost source {Provider} updated (credential {Credential}, enabled {Enabled}).", normalized, existing.CredentialName, enabled);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task<bool> DeleteAsync(string provider, CancellationToken cancellationToken)
    {
        var existing = await sources.GetAsync(CostProviders.Normalize(provider), cancellationToken);
        if (existing is null)
        {
            return false;
        }

        sources.Remove(existing);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Cost source {Provider} deleted.", existing.Provider);
        return true;
    }
}
