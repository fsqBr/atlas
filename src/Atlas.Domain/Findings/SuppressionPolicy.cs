namespace Atlas.Domain.Findings;

/// <summary>
/// A standing decision instead of a one-off triage: "this rule (or this rule
/// under this path) is noise here". Applied to scanner output before
/// reconciliation, so matching findings resolve and never come back; also
/// applied immediately to existing open findings when created. Assessment-scoped
/// or tenant-wide (AssessmentId null). Auditable: who, why, when.
/// </summary>
public sealed class SuppressionPolicy
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid? AssessmentId { get; private set; }

    /// <summary>Exact rule id, or a prefix ending with '*' (e.g. "privacy.pii.*"). '*' alone matches every rule (path required then).</summary>
    public string RulePattern { get; private set; } = null!;

    /// <summary>Optional gitignore-like glob restricting the policy to files under a path; null = whole assessment.</summary>
    public string? PathGlob { get; private set; }

    public string Reason { get; private set; } = null!;
    public string Author { get; private set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private SuppressionPolicy()
    {
    }

    public SuppressionPolicy(Guid id, Guid tenantId, Guid? assessmentId, string rulePattern, string? pathGlob, string reason, string author)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty)
        {
            throw new ArgumentException("Ids must not be empty.");
        }

        rulePattern = rulePattern?.Trim() ?? string.Empty;
        pathGlob = string.IsNullOrWhiteSpace(pathGlob) ? null : pathGlob.Trim();
        if (rulePattern.Length == 0 || (rulePattern == "*" && pathGlob is null))
        {
            throw new ArgumentException("A policy needs a rule pattern, or '*' together with a path.", nameof(rulePattern));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A policy needs a reason.", nameof(reason));
        }

        if (string.IsNullOrWhiteSpace(author))
        {
            throw new ArgumentException("A policy needs an author.", nameof(author));
        }

        Id = id;
        TenantId = tenantId;
        AssessmentId = assessmentId;
        RulePattern = rulePattern;
        PathGlob = pathGlob;
        Reason = reason.Trim();
        Author = author.Trim();
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public bool MatchesRule(string ruleId) =>
        RulePattern == "*"
        || (RulePattern.EndsWith('*') ? ruleId.StartsWith(RulePattern[..^1], StringComparison.Ordinal) : string.Equals(ruleId, RulePattern, StringComparison.Ordinal));

    public bool Matches(string ruleId, string? filePath)
    {
        if (!MatchesRule(ruleId))
        {
            return false;
        }

        if (PathGlob is null)
        {
            return true;
        }

        return filePath is not null && Workspaces.PathExclusions.Compile([PathGlob], includeDefaults: false).IsExcluded(filePath);
    }

    public string Describe() => PathGlob is null ? RulePattern : $"{RulePattern} @ {PathGlob}";
}
