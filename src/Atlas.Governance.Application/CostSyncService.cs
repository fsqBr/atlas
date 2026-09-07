using System.Text;
using Atlas.Application.Credentials;
using Atlas.Application.Tenants;
using Atlas.Governance.Domain;
using Microsoft.Extensions.Logging;

namespace Atlas.Governance.Application;

public sealed record CostSyncResult(string Provider, bool Succeeded, int Facts, int Seats, string? Error);

/// <summary>
/// Pulls the last <see cref="DefaultDays"/> days of cost/usage from every enabled source.
/// Manual, read-only, opt-in: an administrator triggers it; each source is isolated — one provider
/// failing never blocks the others — and each call is bounded by a timeout. The decrypted key exists
/// only for the duration of the provider call.
/// </summary>
public sealed class CostSyncService(
    ICostSourceRepository sources,
    ICostFactRepository facts,
    ISeatFactRepository seats,
    ICredentialRepository credentials,
    ISecretCipher cipher,
    IEnumerable<ICostProviderClient> clients,
    IGovernanceUnitOfWork unitOfWork,
    ITenantContext tenant,
    ILogger<CostSyncService> logger,
    IUsageFactRepository? usage = null,
    IPriceCatalogResolver? prices = null)
{
    public const int DefaultDays = 35;
    public const int MaxDays = 400;
    private static readonly TimeSpan PerSourceTimeout = TimeSpan.FromSeconds(90);

    public async Task<IReadOnlyList<CostSyncResult>> SyncAsync(int days, CancellationToken cancellationToken)
    {
        days = Math.Clamp(days <= 0 ? DefaultDays : days, 1, MaxDays);
        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = to.AddDays(-days);
        var results = new List<CostSyncResult>();
        var byProvider = clients.ToDictionary(c => c.Provider, StringComparer.OrdinalIgnoreCase);

        foreach (var source in (await sources.ListAsync(cancellationToken)).Where(s => s.Enabled).OrderBy(s => s.Provider, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await SyncOneAsync(source, byProvider, from, to, cancellationToken);
            results.Add(result);
            source.RecordSync(result.Succeeded, result.Error, result.Facts + result.Seats);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return results;
    }

    private async Task<CostSyncResult> SyncOneAsync(CostSource source, IReadOnlyDictionary<string, ICostProviderClient> byProvider, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        if (!byProvider.TryGetValue(source.Provider, out var client))
        {
            return new CostSyncResult(source.Provider, false, 0, 0, $"No client registered for provider '{source.Provider}'.");
        }

        if (!cipher.IsConfigured)
        {
            return new CostSyncResult(source.Provider, false, 0, 0, "The secret store is not configured (Atlas:Secrets:MasterKeyBase64).");
        }

        var credential = await credentials.GetByNameAsync(tenant.Require(), source.CredentialName, cancellationToken);
        if (credential is null)
        {
            return new CostSyncResult(source.Provider, false, 0, 0, $"Credential '{source.CredentialName}' no longer exists.");
        }

        string secret;
        try
        {
            secret = Encoding.UTF8.GetString(cipher.Unprotect(credential.Envelope));
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            return new CostSyncResult(source.Provider, false, 0, 0, "The stored credential could not be decrypted (master key changed?).");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PerSourceTimeout);
        try
        {
            var collected = await client.CollectAsync(source, secret, from, to, timeout.Token);
            // Only the requested window is replaced; a client that returns more than asked cannot leak rows outside it.
            var collection = collected with { Facts = collected.Facts.Where(f => f.Period >= from && f.Period <= to).ToList() };
            await facts.ReplaceWindowAsync(source.Id, from, to, collection.Facts, cancellationToken);
            if (usage is not null && (collection.Usage.Count > 0 || source.Provider == CostProviders.Cursor))
            {
                var catalog = prices is null ? null : await prices.ResolveAsync(cancellationToken);
                foreach (var u in collection.Usage)
                {
                    if (catalog is not null)
                    {
                        u.Reprice(catalog.Estimate(u.Model, u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens), catalog.Version);
                    }
                }

                await usage.ReplaceToolWindowAsync(source.Provider, from, to, collection.Usage.Where(u => u.Period >= from && u.Period <= to).ToList(), cancellationToken);
            }
            if (collection.Seats.Count > 0 || source.Provider == CostProviders.GitHubCopilot)
            {
                await seats.ReplaceAsync(source.Id, collection.Seats, cancellationToken);
            }

            credential.MarkUsed();
            logger.LogInformation("Cost sync {Provider}: {Facts} fact(s), {Seats} seat(s) for {From}..{To}.", source.Provider, collection.Facts.Count, collection.Seats.Count, from, to);
            return new CostSyncResult(source.Provider, true, collection.Facts.Count, collection.Seats.Count, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Cost sync {Provider} timed out after {Timeout}.", source.Provider, PerSourceTimeout);
            return new CostSyncResult(source.Provider, false, 0, 0, $"Provider did not answer within {PerSourceTimeout.TotalSeconds:0}s.");
        }
        catch (CostProviderException ex)
        {
            logger.LogWarning("Cost sync {Provider} rejected: {Error}", source.Provider, ex.Message);
            return new CostSyncResult(source.Provider, false, 0, 0, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Cost sync {Provider}: network error.", source.Provider);
            return new CostSyncResult(source.Provider, false, 0, 0, $"Network error talking to the provider: {ex.Message}");
        }
        finally
        {
            secret = string.Empty;
        }
    }
}
