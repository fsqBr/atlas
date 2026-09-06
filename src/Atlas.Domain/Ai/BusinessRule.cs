namespace Atlas.Domain.Ai;

/// <summary>
/// A business rule the LLM recovered from source code ("Business Rule
/// Discovery"). Always traceable to a file and member; always marked as AI
/// output with the model that produced it and its self-reported confidence.
/// Rules are replaced wholesale by each analysis of the assessment.
/// </summary>
public sealed class BusinessRule
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid AssessmentId { get; private set; }
    public Guid AnalysisId { get; private set; }
    public string FilePath { get; private set; } = null!;
    public string Symbol { get; private set; } = null!;
    public int StartLine { get; private set; }
    public string Name { get; private set; } = null!;
    public string DescriptionEn { get; private set; } = null!;
    public string DescriptionPt { get; private set; } = null!;
    public BusinessRuleCategory Category { get; private set; }
    public string ConditionsJson { get; private set; } = "[]";
    public double Confidence { get; private set; }
    public string Model { get; private set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Thumbs up (1) or down (-1) from a reader; null until someone votes. Last vote wins.</summary>
    public int? Rating { get; private set; }
    public string? FeedbackComment { get; private set; }
    public string? RatedBy { get; private set; }
    public DateTimeOffset? RatedAtUtc { get; private set; }

    public const int MaxFeedbackLength = 500;

    public void Rate(int rating, string? comment, string? by)
    {
        if (rating is not (-1 or 0 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(rating), "Rating must be -1, 0 (clear) or 1.");
        }

        if (rating == 0)
        {
            Rating = null;
            FeedbackComment = null;
            RatedBy = null;
            RatedAtUtc = null;
            return;
        }

        Rating = rating;
        var trimmed = comment?.Trim();
        FeedbackComment = string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > MaxFeedbackLength ? trimmed[..MaxFeedbackLength] : trimmed;
        RatedBy = string.IsNullOrWhiteSpace(by) ? null : by.Trim();
        RatedAtUtc = DateTimeOffset.UtcNow;
    }

    private BusinessRule()
    {
    }

    public BusinessRule(
        Guid id, Guid tenantId, Guid assessmentId, Guid analysisId,
        string filePath, string symbol, int startLine,
        string name, string descriptionEn, string descriptionPt,
        BusinessRuleCategory category, string conditionsJson, double confidence, string model)
    {
        Id = id;
        TenantId = tenantId;
        AssessmentId = assessmentId;
        AnalysisId = analysisId;
        FilePath = filePath;
        Symbol = symbol;
        StartLine = startLine;
        Name = Truncate(name, 200);
        DescriptionEn = Truncate(descriptionEn, 2000);
        DescriptionPt = Truncate(descriptionPt, 2000);
        Category = category;
        ConditionsJson = conditionsJson;
        Confidence = Math.Clamp(confidence, 0, 1);
        Model = model;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}

public enum BusinessRuleCategory
{
    Validation = 0,
    Calculation = 1,
    Eligibility = 2,
    Pricing = 3,
    Workflow = 4,
    Authorization = 5,
    DataIntegrity = 6,
    Other = 7,
}

/// <summary>One AI analysis of an assessment: what was sent, what came back, what it cost in tokens.</summary>
public sealed class BusinessRuleAnalysis
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid AssessmentId { get; private set; }
    public AiProvider Provider { get; private set; }
    public string Model { get; private set; } = null!;
    public BusinessRuleAnalysisStatus Status { get; private set; }
    public int CandidatesFound { get; private set; }
    public int SnippetsSent { get; private set; }
    public int RulesFound { get; private set; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    private BusinessRuleAnalysis()
    {
    }

    public BusinessRuleAnalysis(Guid id, Guid tenantId, Guid assessmentId, AiProvider provider, string model)
    {
        Id = id;
        TenantId = tenantId;
        AssessmentId = assessmentId;
        Provider = provider;
        Model = model;
        Status = BusinessRuleAnalysisStatus.Running;
        StartedAtUtc = DateTimeOffset.UtcNow;
    }

    public void Complete(int candidates, int snippetsSent, int rulesFound, long inputTokens, long outputTokens)
    {
        CandidatesFound = candidates;
        SnippetsSent = snippetsSent;
        RulesFound = rulesFound;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        Status = BusinessRuleAnalysisStatus.Completed;
        CompletedAtUtc = DateTimeOffset.UtcNow;
    }

    public void Fail(string error, int candidates, int snippetsSent, long inputTokens, long outputTokens)
    {
        CandidatesFound = candidates;
        SnippetsSent = snippetsSent;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        Error = error.Length > 2000 ? error[..2000] : error;
        Status = BusinessRuleAnalysisStatus.Failed;
        CompletedAtUtc = DateTimeOffset.UtcNow;
    }
}

public enum BusinessRuleAnalysisStatus
{
    Running = 0,
    Completed = 1,
    Failed = 2,
}
