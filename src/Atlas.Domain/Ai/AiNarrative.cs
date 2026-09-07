namespace Atlas.Domain.Ai;

/// <summary>
/// A piece of text the model wrote about an assessment — a finding explanation,
/// the executive summary or the migration plan draft — cached per language so the
/// same question is not paid for twice. Always labelled with the model; never
/// treated as a finding.
/// </summary>
public sealed class AiNarrative
{
    public static class Kinds
    {
        public const string FindingExplanation = "finding-explanation";
        public const string ExecutiveSummary = "executive-summary";
        public const string MigrationPlan = "migration-plan";
        public const string FindingFix = "finding-fix";
        public const string PrSummary = "pr-summary";
    }

    /// <summary>Longest text kept (a migration plan runs to a few pages; explanations are far shorter).</summary>
    public const int MaxTextLength = 20_000;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid AssessmentId { get; private set; }
    public string Kind { get; private set; } = null!;

    /// <summary>Finding fingerprint for explanations and fix suggestions (stable across runs); "summary" for the executive summary; "plan" for the migration plan.</summary>
    public string Key { get; private set; } = null!;
    public string Lang { get; private set; } = null!;
    public string Text { get; private set; } = null!;
    public string Model { get; private set; } = null!;
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private AiNarrative()
    {
    }

    public AiNarrative(Guid id, Guid tenantId, Guid assessmentId, string kind, string key, string lang, string text, string model, long inputTokens, long outputTokens)
    {
        if (kind is not (Kinds.FindingExplanation or Kinds.ExecutiveSummary or Kinds.MigrationPlan or Kinds.FindingFix or Kinds.PrSummary))
        {
            throw new ArgumentException($"Unknown narrative kind '{kind}'.", nameof(kind));
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Narrative text must not be empty.", nameof(text));
        }

        Id = id;
        TenantId = tenantId;
        AssessmentId = assessmentId;
        Kind = kind;
        Key = key;
        Lang = NormalizeLang(lang);
        Text = text.Length > MaxTextLength ? text[..MaxTextLength] : text;
        Model = model;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }


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

    public void Replace(string text, string model, long inputTokens, long outputTokens)
    {
        Text = text.Length > MaxTextLength ? text[..MaxTextLength] : text;
        Model = model;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public static string NormalizeLang(string? lang) => lang is not null && lang.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ? "pt-BR" : "en";
}
