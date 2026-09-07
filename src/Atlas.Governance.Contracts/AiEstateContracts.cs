namespace Atlas.Governance.Contracts;

// ---- Cost sources (admin) ----

public sealed record CostSourceResponse(
    string Provider,
    string CredentialName,
    string? Scope,
    bool Enabled,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastSyncAtUtc,
    string? LastSyncStatus,
    string? LastSyncError,
    int LastSyncFacts);

/// <summary>Connects a provider through a credential already stored under /api/credentials (the key never travels here).</summary>
public sealed record UpsertCostSourceRequest(string CredentialName, string? Scope = null, bool Enabled = true);

public sealed record CostSyncRequest(int Days = 35);

public sealed record CostSyncResultResponse(string Provider, bool Succeeded, int Facts, int Seats, string? Error);

// The cost summary response records live in Atlas.Contracts (the summary port is owned by the core).
