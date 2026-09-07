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

// ---- Developer usage reports (opt-in CLI; aggregates only) ----

public sealed record UsageEntryRequest(DateOnly Period, string Model, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, int Requests, int Sessions);

public sealed record UsageReportRequest(string Actor, string Tool, string? AgentVersion, IReadOnlyList<UsageEntryRequest> Entries);

public sealed record UsageReportResponse(int Accepted, int Rejected, decimal EstimatedCost, string Currency, int UnpricedEntries, string PriceCatalogVersion);

public sealed record UsageActorResponse(string Actor, IReadOnlyList<string> Tools, int Sessions, int Requests, long Tokens, decimal EstimatedCost, long UnpricedTokens, DateTimeOffset LastReportUtc, DateOnly LastPeriod);

public sealed record UsageModelResponse(string Model, long Tokens, decimal? EstimatedCost, int Actors);

public sealed record UsageDayResponse(DateOnly Period, long Tokens, decimal EstimatedCost, int Actors);

public sealed record AiUsageSummaryResponse(
    int Days, DateOnly From, DateOnly To, string PriceCatalogVersion, string Currency,
    int ReportingActors, long TotalTokens, decimal EstimatedCost, long UnpricedTokens,
    IReadOnlyList<UsageActorResponse> Actors, IReadOnlyList<UsageModelResponse> ByModel, IReadOnlyList<UsageDayResponse> ByDay);

public sealed record ModelPriceResponse(string Pattern, decimal Input, decimal Output, decimal? CacheRead, decimal? CacheWrite);

public sealed record PriceCatalogResponse(string Version, string Currency, string? Note, string Source, IReadOnlyList<ModelPriceResponse> Models);
