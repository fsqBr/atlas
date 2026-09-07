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

public sealed record UsageForecastResponse(decimal MonthToDateCost, int DaysElapsedInMonth, int DaysInMonth, decimal ProjectedMonthCost, decimal DailyAverage7, decimal DailyAverage30, decimal ProjectedNext30Cost, decimal? TrendPercent, long MonthToDateTokens, long UnpricedTokensMonthToDate);

public sealed record UsageActorResponse(string Actor, IReadOnlyList<string> Tools, int Sessions, int Requests, long Tokens, decimal EstimatedCost, long UnpricedTokens, DateTimeOffset LastReportUtc, DateOnly LastPeriod, UsageForecastResponse Forecast);

public sealed record LiveActorResponse(string Actor, IReadOnlyList<string> Tools, DateTimeOffset LastReportUtc, bool ActiveNow, long TokensToday, decimal EstimatedCostToday, int RequestsToday, long TokensLastHour);

public sealed record LivePointResponse(DateTimeOffset AtUtc, long Tokens, decimal EstimatedCost);

public sealed record LiveUsageResponse(DateOnly Period, DateTimeOffset AsOfUtc, int ActiveMinutes, int ActiveActors, int ReportingActorsToday, long TokensToday, decimal EstimatedCostToday, long TokensLastHour, IReadOnlyList<LiveActorResponse> Actors, IReadOnlyList<LivePointResponse> Curve);

public sealed record UsageModelResponse(string Model, long Tokens, decimal? EstimatedCost, int Actors);

public sealed record UsageDayResponse(DateOnly Period, long Tokens, decimal EstimatedCost, int Actors);

public sealed record AiUsageSummaryResponse(
    int Days, DateOnly From, DateOnly To, string PriceCatalogVersion, string Currency,
    int ReportingActors, long TotalTokens, decimal EstimatedCost, long UnpricedTokens,
    IReadOnlyList<UsageActorResponse> Actors, IReadOnlyList<UsageModelResponse> ByModel, IReadOnlyList<UsageDayResponse> ByDay, UsageForecastResponse Forecast);

public sealed record ModelPriceResponse(Guid? Id, string Pattern, decimal Input, decimal Output, decimal? CacheRead, decimal? CacheWrite, string Source, string? Note, string? UpdatedBy, DateTimeOffset? UpdatedAtUtc);

public sealed record UnpricedModelResponse(string Model, long Tokens, int Actors, DateOnly LastSeen);

public sealed record PriceCatalogResponse(string Version, string BaseVersion, string Currency, string? Note, IReadOnlyList<ModelPriceResponse> Prices, IReadOnlyList<UnpricedModelResponse> Unpriced);

/// <summary>Create or update a tenant price line. Prices are USD per million tokens; the pattern is a regex or a plain model id.</summary>
public sealed record UpsertModelPriceRequest(string Pattern, decimal Input, decimal Output, decimal? CacheRead, decimal? CacheWrite, string? Note, string? Author);
