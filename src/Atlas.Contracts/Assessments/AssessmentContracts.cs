namespace Atlas.Contracts.Assessments;

public sealed record CreateAssessmentRequest(string Name, string SourceKind, string SourceLocator, string? Branch, string? CredentialName = null, IReadOnlyList<string>? ExcludePaths = null);

public sealed record ScopeRequest(IReadOnlyList<string> ExcludePaths);

public sealed record ReplaceUploadRequest(string UploadId);

public sealed record AssessmentCreatedResponse(Guid Id, Guid JobId);

public sealed record RenameAssessmentRequest(string Name);

public sealed record AssessmentSummaryResponse(
    Guid Id,
    string Name,
    string SourceKind,
    string SourceLocator,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int? HealthScore,
    string? RiskLevel,
    int? OpenFindings,
    string? ActiveJobState);

/// <summary>A folder under the read-only local sources mount that the "local" connector can assess.</summary>
public sealed record LocalSourceResponse(string Name, string Path, bool HasDotNetProjects);

public sealed record AssessmentResponse(
    Guid Id,
    string Name,
    string SourceKind,
    string SourceLocator,
    string? Branch,
    string? CredentialName,
    IReadOnlyList<string> ExcludePaths,
    int? RerunEveryDays,
    string? WebhookUrl,
    int? TargetScore,
    DateTimeOffset? TargetDate,
    string Status,
    string? FailureReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    IReadOnlyList<ScanResponse> Scans,
    /// <summary>Queued / Leased / Running while a (re)run is pending or in progress; null when idle.</summary>
    string? ActiveJobState);

public sealed record ScanResponse(
    Guid Id,
    string ScannerId,
    string ScannerVersion,
    string? CommitSha,
    string Status,
    string? Error,
    int FindingsEmitted,
    int FindingsNew,
    int FindingsRecurring,
    int FindingsResolved,
    int FindingsRegressed,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc);

public sealed record FindingResponse(
    Guid Id,
    string RuleId,
    string Category,
    string Severity,
    string Status,
    string Origin,
    string Title,
    string? Message,
    string? Confidence,
    string? Remediation,
    string? FilePath,
    int? LineStart,
    int? LineEnd,
    string? Symbol,
    string? ScannerId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    SuppressionResponse? Suppression);

public sealed record SuppressionResponse(string Kind, string Reason, string Author, DateTimeOffset CreatedAtUtc);

/// <summary>Action: Suppress | FalsePositive | Reopen. Reason is required for Suppress/FalsePositive.</summary>
public sealed record TriageRequest(string Action, string? Reason, string? Author);

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public sealed record HealthResponse(
    int Score,
    string RiskLevel,
    string ModelVersion,
    string Explanation,
    int OpenFindings,
    int ProjectCount,
    string? CommitSha,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<HealthDimensionResponse> Dimensions);

public sealed record HealthDimensionResponse(
    string Name,
    double Weight,
    int Score,
    double Penalty,
    IReadOnlyList<HealthContributorResponse> Contributors);

public sealed record HealthContributorResponse(string RuleId, int Count, double Points);

public sealed record RunResponse(
    Guid Id,
    int Number,
    string? CommitSha,
    string Status,
    string? FailureReason,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    int? HealthScore,
    int? OpenFindings,
    int FindingsNew,
    int FindingsRecurring,
    int FindingsResolved,
    int FindingsRegressed,
    int ScannersRun,
    int ScannersFailed);

public sealed record RunQueuedResponse(Guid JobId);

public sealed record DimensionDeltaResponse(string Name, int? Before, int After, int? Delta);

public sealed record RuleDeltaResponse(
    string RuleId,
    string Title,
    string Category,
    string MaxSeverity,
    int Count,
    IReadOnlyList<string> SampleLocations);

public sealed record InventoryDeltaResponse(
    long LinesBefore, long LinesAfter, int FilesBefore, int FilesAfter, int ProjectsBefore, int ProjectsAfter);

public sealed record RunComparisonResponse(
    RunResponse Current,
    RunResponse? Previous,
    bool SameCommit,
    int? HealthDelta,
    IReadOnlyList<DimensionDeltaResponse> Dimensions,
    IReadOnlyList<RuleDeltaResponse> Resolved,
    IReadOnlyList<RuleDeltaResponse> New,
    IReadOnlyList<RuleDeltaResponse> Regressed,
    InventoryDeltaResponse? Inventory);

/// <summary>Credential metadata. The secret is write-only: it never appears in any response.</summary>
public sealed record CredentialResponse(
    string Name,
    string? Username,
    string? Description,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastUsedAtUtc,
    int UsedByAssessments);

public sealed record UpsertCredentialRequest(string Secret, string? Username, string? Description);

public sealed record DiscoverSourcesRequest(string SourceKind, string Locator, string? CredentialName);

public sealed record DiscoveredRepositoryResponse(
    string Name,
    string Locator,
    string Kind,
    string? DefaultBranch,
    bool Archived,
    string? Language,
    DateTimeOffset? LastPushUtc,
    bool IsPrivate);

// ---- Modernization (strategy comparison, cost ranges, roadmap) ----

public sealed record ModernizationProfileResponse(
    long LinesOfCode, int Projects, int LegacyFrameworkProjects, int ModernFrameworkProjects, int UnknownFrameworkProjects,
    int LegacyProjectFormat, int PrerequisiteBlockers, int HighBlockers, int MediumBlockers, int ProjectsWithBlockers,
    int CriticalSecurity, int HighSecurity, int MediumSecurity, int SecretsFound, int VulnerablePackages,
    bool HasTests, double? CoverageLineRate, int ProjectsWithoutTests, int ArchitectureCycles, string? Tier);

public sealed record RangeResponse(double Optimistic, double Likely, double Conservative);

public sealed record MoneyRangeResponse(decimal Optimistic, decimal Likely, decimal Conservative, string Currency);

public sealed record EffortItemResponse(string Key, string Label, double Hours, double Quantity);

public sealed record AssumptionResponse(string Key, string Label, string Value);

public sealed record EstimateResponse(
    string ModelVersion,
    RangeResponse EffortHours,
    RangeResponse DurationMonths,
    MoneyRangeResponse Cost,
    string Confidence,
    string ConfidenceLabel,
    IReadOnlyList<EffortItemResponse> Breakdown,
    IReadOnlyList<AssumptionResponse> Assumptions);

public sealed record StrategyResponse(
    string Strategy,
    string Name,
    string Description,
    int FitScore,
    string Risk,
    bool Recommended,
    IReadOnlyList<string> Rationale,
    IReadOnlyList<string> Prerequisites,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Benefits,
    EstimateResponse Estimate);

public sealed record WorkItemResponse(string Key, string Label, int Quantity);

public sealed record PhaseResponse(
    string Key,
    string Name,
    int Order,
    double EffortShare,
    RangeResponse EffortHours,
    RangeResponse DurationMonths,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> DependsOnNames,
    IReadOnlyList<WorkItemResponse> WorkItems);

public sealed record RoadmapResponse(string ModelVersion, string Strategy, IReadOnlyList<PhaseResponse> Phases);

public sealed record ModernizationPlanResponse(
    string ModelVersion,
    ModernizationProfileResponse Profile,
    string Recommended,
    string RecommendedName,
    IReadOnlyList<StrategyResponse> Strategies,
    RoadmapResponse Roadmap);

// ---- Portfolio (estate view) ----

public sealed record PortfolioRuleResponse(string RuleId, string Title, string Category, string MaxSeverity, int Count, int Assessments);

public sealed record PortfolioFrameworkResponse(string Framework, int Count, bool Legacy);

public sealed record PortfolioRowResponse(
    Guid Id, string Name, string SourceKind, string Status, int? Score, string? Risk, int? OpenFindings,
    long Lines, int Projects, int LegacyProjects, DateTimeOffset? CompletedAtUtc,
    int? Percentile, int? TargetScore, DateTimeOffset? TargetDate, string TargetStatus);

public sealed record PortfolioTrendPointResponse(DateOnly Date, double? AverageScore, int OpenFindings, int Assessed, Dictionary<string, double>? Dimensions = null);

public sealed record RuleCatalogEntryResponse(
    string Id,
    string ScannerId,
    string Category,
    string DefaultSeverity,
    string? OverrideSeverity,
    string Title,
    string Description,
    string? Remediation,
    int OpenFindings,
    int Assessments);

public sealed record RuleSeverityRequest(string? Severity, string? Author);

public sealed record BenchmarkDimensionResponse(string Name, int Count, double P25, double P50, double P75, int Best, int Worst);

public sealed record PortfolioResponse(
    int Assessments,
    int Assessed,
    double? AverageScore,
    IReadOnlyDictionary<string, int> ByRisk,
    long Lines,
    int Files,
    int Projects,
    int LegacyProjects,
    int ModernProjects,
    int UnknownProjects,
    IReadOnlyList<PortfolioFrameworkResponse> Frameworks,
    int OpenFindings,
    IReadOnlyDictionary<string, int> OpenBySeverity,
    IReadOnlyDictionary<string, int> OpenByCategory,
    IReadOnlyList<PortfolioRuleResponse> TopRules,
    IReadOnlyList<PortfolioRowResponse> Rows,
    IReadOnlyList<BenchmarkDimensionResponse> Benchmark,
    IReadOnlyDictionary<string, int> Targets);

// ---- Suppression policies ----

public sealed record SuppressionPolicyResponse(Guid Id, Guid? AssessmentId, string RulePattern, string? PathGlob, string Reason, string Author, DateTimeOffset CreatedAtUtc);

public sealed record CreatePolicyRequest(string RulePattern, string? PathGlob, string Reason, string Author);

public sealed record PolicyCreatedResponse(SuppressionPolicyResponse Policy, int AppliedToExisting);

// ---- Cost calibration ----

public sealed record RecordActualRequest(string Strategy, double ActualHours, double? ActualMonths, decimal? ActualCost, string? Currency, string? Notes, string RecordedBy, double? EstimatedHours = null);

public sealed record ActualResponse(Guid AssessmentId, string Strategy, string StrategyName, double ActualHours, double? ActualMonths, decimal? ActualCost, string Currency, string? Notes, string RecordedBy, DateTimeOffset RecordedAtUtc);

public sealed record CalibrationPointResponse(Guid AssessmentId, string AssessmentName, string Strategy, string StrategyName, double EstimatedLikelyHours, double ActualHours, double Ratio, string? Notes, DateTimeOffset RecordedAtUtc);

public sealed record CalibrationResponse(int Points, double? MeanRatio, double? MedianRatio, string Recommendation, string RecommendationText, IReadOnlyList<CalibrationPointResponse> Items);

// ---- Views, schedule, jobs ----

public sealed record RuleGroupResponse(string RuleId, string Title, string Category, string MaxSeverity, int Count, IReadOnlyList<string> SampleFiles);

public sealed record HeatmapRowResponse(string Folder, int Open, int Critical, int High, int Medium, int Low, int Informational, int Files);

public sealed record ScheduleRequest(int? RerunEveryDays, string? WebhookUrl, int? TargetScore = null, DateTimeOffset? TargetDate = null);

public sealed record JobResponse(Guid Id, Guid AssessmentId, string? AssessmentName, string Kind, string State, int Attempt, string? Error, DateTimeOffset QueuedAtUtc, DateTimeOffset? StartedAtUtc, DateTimeOffset? FinishedAtUtc, string? LeasedBy);

public sealed record AuditEntryResponse(long Id, DateTimeOffset AtUtc, string Actor, string Method, string Path, int StatusCode, Guid? AssessmentId, string? Detail);

// ---- Local source browser ----

public sealed record LocalRootResponse(string Path, string Label, bool Exists);

public sealed record LocalFolderResponse(string Name, string Path, bool HasDotNetProjects, bool HasSolution, bool IsGitRepo);

public sealed record BrowseResponse(IReadOnlyList<LocalRootResponse> Roots, string? Current, string? Parent, IReadOnlyList<LocalFolderResponse> Entries);

// ---- AI ----

public sealed record AiSettingsRequest(string Provider, string? Model, string? BaseUrl, string? ApiKey, bool Enabled, int? MaxSnippetsPerAnalysis);

public sealed record BusinessRuleResponse(
    Guid Id, string FilePath, string Symbol, int StartLine, string Name, string Description, string Category,
    IReadOnlyList<string> Conditions, double Confidence, string Model, DateTimeOffset CreatedAtUtc, int? Rating = null, string? FeedbackComment = null);

public sealed record BusinessRuleAnalysisResponse(
    Guid Id, string Provider, string Model, string Status, int CandidatesFound, int SnippetsSent, int RulesFound,
    long InputTokens, long OutputTokens, string? Error, DateTimeOffset StartedAtUtc, DateTimeOffset? CompletedAtUtc);

public sealed record BusinessRulesResponse(bool AiUsable, IReadOnlyList<BusinessRuleAnalysisResponse> Analyses, IReadOnlyList<BusinessRuleResponse> Rules);

public sealed record NarrativeResponse(string Text, string Model, bool Cached, DateTimeOffset CreatedAtUtc, int? Rating = null, string? FeedbackComment = null);

/// <summary>Thumbs up (1), down (-1) or clear (0) on something the model wrote; the comment is optional.</summary>
public sealed record FeedbackRequest(int Rating, string? Comment, string? Author);

public sealed record FeedbackBucketResponse(string Key, int Up, int Down, double? HelpfulShare);

public sealed record FeedbackEntryResponse(string Kind, string Model, int Rating, string? Comment, Guid AssessmentId, string? RatedBy, DateTimeOffset RatedAtUtc, string Title);

public sealed record AiFeedbackSummaryResponse(int Up, int Down, IReadOnlyList<FeedbackBucketResponse> ByKind, IReadOnlyList<FeedbackBucketResponse> ByModel, IReadOnlyList<FeedbackEntryResponse> Recent);

/// <summary>A fix suggestion if one exists, plus the state of the most recent fix job for the finding (Queued / Running / Succeeded / DeadLetter).</summary>
public sealed record FindingFixResponse(NarrativeResponse? Fix, string? JobState, string? JobError);

public sealed record AiEstimateResponse(int Methods, int Requests, long InputTokens, long OutputTokens, string Note);

public sealed record TenantRequest(string Name, string? ExternalKey);

public sealed record TenantResponse(Guid Id, string Name, string? ExternalKey, DateTimeOffset CreatedAtUtc, bool IsDefault);

public sealed record QualityGateResponse(bool Passed, bool Evaluated, int? Score, IReadOnlyDictionary<string, int> OpenBySeverity, IReadOnlyList<string> Violations, string? FailOn, int? MinScore, string? ReportUrl);

public sealed record ApiTokenRequest(string Name, string Role, DateTimeOffset? ExpiresAtUtc);

public sealed record ApiTokenResponse(Guid Id, string Name, string Hint, string Role, string CreatedBy, DateTimeOffset CreatedAtUtc, DateTimeOffset? ExpiresAtUtc, DateTimeOffset? LastUsedAtUtc, DateTimeOffset? RevokedAtUtc, bool Active);

public sealed record ApiTokenCreatedResponse(ApiTokenResponse Token, string Secret);

public sealed record AccessGrantRequest(string Subject, string? SubjectName, string Role);

public sealed record AccessEntryResponse(Guid Id, string Subject, string? SubjectName, string Role, string GrantedBy, DateTimeOffset GrantedAtUtc);

public sealed record AccessResponse(bool Restricted, string? MyRole, bool CanManage, bool CanEdit, IReadOnlyList<AccessEntryResponse> Entries);

public sealed record ComparisonSideResponse(
    Guid Id, string Name, string SourceKind, string Status, DateTimeOffset? CompletedAtUtc, int? Score, string? Risk,
    IReadOnlyDictionary<string, int> Dimensions, int OpenFindings, IReadOnlyDictionary<string, int> OpenBySeverity, IReadOnlyDictionary<string, int> OpenByCategory,
    long Lines, int Files, int Projects, int LegacyProjects, IReadOnlyDictionary<string, int> UiFrameworks,
    string? RecommendedStrategy, double? LikelyHours, decimal? LikelyCost, string? Currency, int? TargetScore, IReadOnlyDictionary<string, int> TopRules);

public sealed record RuleDifferenceResponse(string RuleId, string Title, string Category, string MaxSeverity, int CountA, int CountB);

public sealed record SideBySideResponse(ComparisonSideResponse A, ComparisonSideResponse B, IReadOnlyList<RuleDifferenceResponse> RuleDifferences);
