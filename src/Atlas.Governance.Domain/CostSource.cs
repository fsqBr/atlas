namespace Atlas.Governance.Domain;

/// <summary>The cost/usage providers the module can read from. Ids are stable API strings, not display names.</summary>
public static class CostProviders
{
    public const string OpenAi = "openai";
    public const string Anthropic = "anthropic";
    public const string GitHubCopilot = "github-copilot";
    public const string Cursor = "cursor";

    public static readonly IReadOnlyList<string> All = [OpenAi, Anthropic, GitHubCopilot, Cursor];

    public static bool IsKnown(string? provider) => provider is not null && All.Contains(provider.Trim().ToLowerInvariant());

    public static string Normalize(string provider) => provider.Trim().ToLowerInvariant();
}

/// <summary>
/// A read-only connection to one provider's billing/usage API. The secret itself lives in the
/// platform's encrypted <c>ConnectorCredential</c> store and is referenced by name — this row never holds it.
/// One source per provider per tenant; sync is manual and idempotent.
/// </summary>
public sealed class CostSource
{
    public const int MaxScopeLength = 200;

    private CostSource()
    {
    }

    public CostSource(Guid id, Guid tenantId, string provider, string credentialName, string? scope)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty)
        {
            throw new ArgumentException("Ids must not be empty.");
        }

        if (!CostProviders.IsKnown(provider))
        {
            throw new ArgumentException($"Unknown cost provider '{provider}'. Known: {string.Join(", ", CostProviders.All)}.", nameof(provider));
        }

        Id = id;
        TenantId = tenantId;
        Provider = CostProviders.Normalize(provider);
        CreatedAtUtc = DateTimeOffset.UtcNow;
        Enabled = true;
        Update(credentialName, scope, enabled: true);
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Provider { get; private set; } = null!;

    /// <summary>Name of the ConnectorCredential holding the admin/billing key.</summary>
    public string CredentialName { get; private set; } = null!;

    /// <summary>Provider-specific scope: GitHub organization login; optional workspace/project hint for others.</summary>
    public string? Scope { get; private set; }

    public bool Enabled { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public DateTimeOffset? LastSyncAtUtc { get; private set; }

    /// <summary>Succeeded | Failed | null (never synced).</summary>
    public string? LastSyncStatus { get; private set; }

    /// <summary>Provider error, truncated and never containing the secret.</summary>
    public string? LastSyncError { get; private set; }

    public int LastSyncFacts { get; private set; }

    public void Update(string credentialName, string? scope, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(credentialName))
        {
            throw new ArgumentException("Credential name is required.", nameof(credentialName));
        }

        if (scope is { Length: > MaxScopeLength })
        {
            throw new ArgumentException($"Scope must be at most {MaxScopeLength} characters.", nameof(scope));
        }

        CredentialName = credentialName.Trim();
        Scope = string.IsNullOrWhiteSpace(scope) ? null : scope.Trim();
        Enabled = enabled;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public void RecordSync(bool succeeded, string? error, int facts)
    {
        LastSyncAtUtc = DateTimeOffset.UtcNow;
        LastSyncStatus = succeeded ? "Succeeded" : "Failed";
        LastSyncError = error is null ? null : error.Length <= 500 ? error : error[..500];
        LastSyncFacts = facts;
    }
}
