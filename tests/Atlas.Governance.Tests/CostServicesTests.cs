using System.Text;
using Atlas.Application.Credentials;
using Atlas.Application.Tenants;
using Atlas.Domain.Credentials;
using Atlas.Governance.Application;
using Atlas.Governance.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.Governance.Tests;

public class CostServicesTests
{
    private static readonly Guid Tenant = Atlas.Domain.Tenants.WellKnownTenants.DefaultId;

    // ---- fakes ----

    private sealed class Sources : ICostSourceRepository
    {
        public List<CostSource> Items { get; } = [];

        public Task<IReadOnlyList<CostSource>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CostSource>>(Items.ToList());

        public Task<CostSource?> GetAsync(string provider, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(s => s.Provider == provider));

        public void Add(CostSource source) => Items.Add(source);

        public void Remove(CostSource source) => Items.Remove(source);
    }

    private sealed class Facts : ICostFactRepository
    {
        public List<CostFact> Items { get; } = [];

        public Task ReplaceWindowAsync(Guid sourceId, DateOnly from, DateOnly to, IReadOnlyList<CostFact> facts, CancellationToken ct)
        {
            Items.RemoveAll(f => f.SourceId == sourceId && f.Period >= from && f.Period <= to);
            Items.AddRange(facts);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CostFact>> ListAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CostFact>>(Items.Where(f => f.Period >= from && f.Period <= to).ToList());
    }

    private sealed class Seats : ISeatFactRepository
    {
        public List<SeatFact> Items { get; } = [];

        public Task ReplaceAsync(Guid sourceId, IReadOnlyList<SeatFact> seats, CancellationToken ct)
        {
            Items.RemoveAll(s => s.SourceId == sourceId);
            Items.AddRange(seats);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SeatFact>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SeatFact>>(Items.ToList());
    }

    private sealed class Credentials : ICredentialRepository
    {
        public List<ConnectorCredential> Items { get; } = [];

        public Task<ConnectorCredential?> GetByNameAsync(Guid tenantId, string name, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(c => c.Name == name));

        public Task<IReadOnlyList<ConnectorCredential>> ListAsync(Guid tenantId, CancellationToken ct) => Task.FromResult<IReadOnlyList<ConnectorCredential>>(Items);

        public Task<int> CountAssessmentsUsingAsync(Guid tenantId, string name, CancellationToken ct) => Task.FromResult(0);

        public void Add(ConnectorCredential credential) => Items.Add(credential);

        public void Remove(ConnectorCredential credential) => Items.Remove(credential);
    }

    /// <summary>Reversible "cipher" for tests: the envelope is the UTF-8 bytes reversed.</summary>
    private sealed class Cipher : ISecretCipher
    {
        public bool IsConfigured => true;

        public byte[] Protect(ReadOnlySpan<byte> plaintext) => plaintext.ToArray().Reverse().ToArray();

        public byte[] Unprotect(ReadOnlySpan<byte> envelope) => envelope.ToArray().Reverse().ToArray();
    }

    private sealed class UnitOfWork : IGovernanceUnitOfWork
    {
        public int Saves { get; private set; }

        public Task SaveChangesAsync(CancellationToken ct)
        {
            Saves++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClient(string provider, Func<CostSource, string, DateOnly, DateOnly, CostCollection> collect) : ICostProviderClient
    {
        public string Provider => provider;

        public List<string> SecretsSeen { get; } = [];

        public Task<CostCollection> CollectAsync(CostSource source, string secret, DateOnly from, DateOnly to, CancellationToken ct)
        {
            SecretsSeen.Add(secret);
            return Task.FromResult(collect(source, secret, from, to));
        }
    }

    private static ConnectorCredential Credential(string name, string secret) =>
        new(Guid.NewGuid(), Tenant, name, null, null, new Cipher().Protect(Encoding.UTF8.GetBytes(secret)));

    // ---- CostSource ----

    [Fact]
    public void Cost_source_validates_provider_and_credential()
    {
        Assert.Throws<ArgumentException>(() => new CostSource(Guid.NewGuid(), Tenant, "bedrock", "c", null));
        Assert.Throws<ArgumentException>(() => new CostSource(Guid.NewGuid(), Tenant, "openai", " ", null));
        var source = new CostSource(Guid.NewGuid(), Tenant, " OpenAI ", "openai-admin", null);
        Assert.Equal("openai", source.Provider);
        Assert.True(source.Enabled);
        source.RecordSync(false, new string('x', 900), 0);
        Assert.Equal(500, source.LastSyncError!.Length);
        Assert.Equal("Failed", source.LastSyncStatus);
    }

    [Fact]
    public async Task Cost_source_service_requires_an_existing_credential_and_scope_for_copilot()
    {
        var sources = new Sources();
        var credentials = new Credentials();
        credentials.Add(Credential("gh-billing", "ghp_x"));
        var service = new CostSourceService(sources, credentials, new UnitOfWork(), SystemTenantContext.Instance, NullLogger<CostSourceService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpsertAsync("openai", "missing", null, true, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpsertAsync("github-copilot", "gh-billing", null, true, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpsertAsync("unknown-provider", "gh-billing", null, true, CancellationToken.None));

        var created = await service.UpsertAsync("github-copilot", "gh-billing", "acme", true, CancellationToken.None);
        Assert.Equal("acme", created.Scope);
        var updated = await service.UpsertAsync("GitHub-Copilot", "gh-billing", "acme-2", false, CancellationToken.None);
        Assert.Same(created, updated);
        Assert.False(updated.Enabled);
        Assert.Single(sources.Items);
        Assert.True(await service.DeleteAsync("github-copilot", CancellationToken.None));
        Assert.False(await service.DeleteAsync("github-copilot", CancellationToken.None));
    }

    // ---- CostSyncService ----

    [Fact]
    public async Task Sync_isolates_provider_failures_and_never_leaks_the_secret()
    {
        var sources = new Sources();
        var openai = new CostSource(Guid.NewGuid(), Tenant, "openai", "openai-admin", null);
        var anthropic = new CostSource(Guid.NewGuid(), Tenant, "anthropic", "anthropic-admin", null);
        var disabled = new CostSource(Guid.NewGuid(), Tenant, "github-copilot", "gh", "acme");
        disabled.Update("gh", "acme", enabled: false);
        sources.Items.AddRange([openai, anthropic, disabled]);

        var credentials = new Credentials();
        credentials.Add(Credential("openai-admin", "sk-admin-SECRET-VALUE"));
        credentials.Add(Credential("anthropic-admin", "sk-ant-admin-SECRET"));

        var facts = new Facts();
        var okClient = new FakeClient("openai", (s, _, from, to) => new CostCollection(
            [new CostFact(Guid.NewGuid(), s.TenantId, s.Id, "openai", CostBasis.ProviderReported, to, "project", "p1", "GPT-4o", 10m, "USD", null, null, "v1"),
             new CostFact(Guid.NewGuid(), s.TenantId, s.Id, "openai", CostBasis.ProviderReported, from.AddDays(-5), "project", "old", null, 99m, "USD", null, null, "v1")], []));
        var failing = new FakeClient("anthropic", (_, secret, _, _) => throw new CostProviderException("anthropic: the provider rejected the credential (HTTP 401)."));

        var uow = new UnitOfWork();
        var sync = new CostSyncService(sources, facts, new Seats(), credentials, new Cipher(), [okClient, failing], uow, SystemTenantContext.Instance, NullLogger<CostSyncService>.Instance);

        var results = await sync.SyncAsync(30, CancellationToken.None);

        Assert.Equal(2, results.Count); // the disabled source is skipped
        var ok = results.Single(r => r.Provider == "openai");
        Assert.True(ok.Succeeded);
        Assert.Equal(1, ok.Facts); // the out-of-window fact never reaches the store
        Assert.Equal(["sk-admin-SECRET-VALUE"], okClient.SecretsSeen); // decrypted only for the call
        Assert.Single(facts.Items); // the out-of-window fact is dropped by the window replace
        Assert.Equal("Succeeded", openai.LastSyncStatus);

        var failed = results.Single(r => r.Provider == "anthropic");
        Assert.False(failed.Succeeded);
        Assert.Contains("401", failed.Error);
        Assert.DoesNotContain("SECRET", failed.Error);
        Assert.Equal("Failed", anthropic.LastSyncStatus);
        Assert.Equal(2, uow.Saves);

        // Re-running is idempotent: same window, same single fact.
        await sync.SyncAsync(30, CancellationToken.None);
        Assert.Single(facts.Items);
    }

    [Fact]
    public async Task Sync_reports_missing_client_or_credential_instead_of_throwing()
    {
        var sources = new Sources();
        sources.Items.Add(new CostSource(Guid.NewGuid(), Tenant, "openai", "gone", null));
        var sync = new CostSyncService(sources, new Facts(), new Seats(), new Credentials(), new Cipher(), [new FakeClient("openai", (_, _, _, _) => CostCollection.Empty)], new UnitOfWork(), SystemTenantContext.Instance, NullLogger<CostSyncService>.Instance);
        var result = Assert.Single(await sync.SyncAsync(7, CancellationToken.None));
        Assert.False(result.Succeeded);
        Assert.Contains("no longer exists", result.Error);

        var noClient = new CostSyncService(sources, new Facts(), new Seats(), new Credentials(), new Cipher(), [], new UnitOfWork(), SystemTenantContext.Instance, NullLogger<CostSyncService>.Instance);
        Assert.Contains("No client registered", Assert.Single(await noClient.SyncAsync(7, CancellationToken.None)).Error);
    }

    // ---- Summary ----

    [Fact]
    public void Summary_keeps_bases_apart_and_counts_idle_seats()
    {
        var openai = new CostSource(Guid.NewGuid(), Tenant, "openai", "a", null);
        openai.RecordSync(true, null, 3);
        var copilot = new CostSource(Guid.NewGuid(), Tenant, "github-copilot", "b", "acme");
        var anthropic = new CostSource(Guid.NewGuid(), Tenant, "anthropic", "c", null);
        anthropic.RecordSync(false, "anthropic: HTTP 401", 0);
        var today = new DateOnly(2025, 9, 6);
        var now = new DateTimeOffset(2025, 9, 6, 12, 0, 0, TimeSpan.Zero);

        var facts = new List<CostFact>
        {
            new(Guid.NewGuid(), Tenant, openai.Id, "openai", CostBasis.ProviderReported, today.AddDays(-1), "project", "p1", "GPT-4o", 10m, "USD", null, null, "v1"),
            new(Guid.NewGuid(), Tenant, openai.Id, "openai", CostBasis.ProviderReported, today, "project", "p1", "GPT-4o", 5m, "USD", null, null, "v1"),
            new(Guid.NewGuid(), Tenant, openai.Id, "openai", CostBasis.ProviderReported, today, "project", "p2", "Embeddings", 1m, "USD", null, null, "v1"),
            new(Guid.NewGuid(), Tenant, copilot.Id, "github-copilot", CostBasis.Estimated, new DateOnly(2025, 9, 1), "seats", "business", "list price", 57m, "USD", 3, "seats", "copilot-list"),
            new(Guid.NewGuid(), Tenant, anthropic.Id, "anthropic", CostBasis.Estimated, today, "claude-code", "active-developers", null, 0m, "USD", 4, "developers", "cc"),
            new(Guid.NewGuid(), Tenant, anthropic.Id, "anthropic", CostBasis.Estimated, today.AddDays(-1), "claude-code", "active-developers", null, 0m, "USD", 2, "developers", "cc"),
            new(Guid.NewGuid(), Tenant, anthropic.Id, "anthropic", CostBasis.Estimated, today, "claude-code", "claude-sonnet-4", "estimated", 3m, "USD", 1800, "tokens", "cc"),
        };
        var seats = new List<SeatFact>
        {
            new(Guid.NewGuid(), Tenant, copilot.Id, "github-copilot", "k1", "business", now.AddDays(-2), false),
            new(Guid.NewGuid(), Tenant, copilot.Id, "github-copilot", "k2", "business", now.AddDays(-90), true),
            new(Guid.NewGuid(), Tenant, copilot.Id, "github-copilot", "k3", "business", null, false),
        };

        var summary = AiCostSummaryBuilder.Build(30, today.AddDays(-30), today, [openai, copilot, anthropic], facts, seats, now);

        var reported = summary.Providers.Single(p => p.Provider == "openai");
        Assert.Equal("ProviderReported", reported.Basis);
        Assert.Equal(16m, reported.Total);
        Assert.Equal(("p1 · GPT-4o", 15m), reported.TopDimensions[0]);
        Assert.Equal("Succeeded", reported.LastSyncStatus);

        var estimatedCopilot = summary.Providers.Single(p => p.Provider == "github-copilot");
        Assert.Equal("Estimated", estimatedCopilot.Basis);
        Assert.Equal(57m, estimatedCopilot.Total);

        var estimatedAnthropic = summary.Providers.Single(p => p.Provider == "anthropic");
        Assert.Equal(3m, estimatedAnthropic.Total); // activity counters are not money
        Assert.Equal("anthropic: HTTP 401", estimatedAnthropic.LastSyncError);

        var seatSummary = Assert.Single(summary.Seats);
        Assert.Equal(3, seatSummary.Total);
        Assert.Equal(1, seatSummary.ActiveWithinIdleWindow);
        Assert.Equal(2, seatSummary.Idle);
        Assert.Equal(1, seatSummary.PendingCancellation);

        var developers = Assert.Single(summary.Activity, a => a.Key == "active-developers");
        Assert.Equal(3m, developers.AveragePerDay);
        Assert.Equal(3, summary.SourcesConfigured);
        Assert.False(summary.IsEmpty);
    }
}
