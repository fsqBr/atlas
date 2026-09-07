using Atlas.Domain.Sources;

namespace Atlas.Domain.Assessments;

/// <summary>
/// One evaluation of one source. Findings are scoped to an assessment; repeated
/// scans of the same assessment reconcile against its existing findings.
/// </summary>
public sealed class Assessment
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = null!;
    public string SourceKind { get; private set; } = null!;
    public string SourceLocator { get; private set; } = null!;
    public string? Branch { get; private set; }
    public string? CredentialName { get; private set; }

    /// <summary>JSON array of gitignore-like globs excluded from analysis (on top of defaults and .atlasignore).</summary>
    public string? ExcludeGlobsJson { get; private set; }

    /// <summary>Re-run cadence in days (null = manual only).</summary>
    public int? RerunEveryDays { get; private set; }

    /// <summary>Webhook receiving run.completed events for this assessment.</summary>
    public string? WebhookUrl { get; private set; }

    /// <summary>Health-score goal ("reach 70") and, optionally, when ("by 2026-12-31"). Evaluated by <see cref="Targets"/>.</summary>
    public int? TargetScore { get; private set; }
    public DateTimeOffset? TargetDate { get; private set; }
    public AssessmentStatus Status { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? StartedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    private Assessment()
    {
    }

    public Assessment(Guid id, Guid tenantId, string name, SourceReference source)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Assessment id must not be empty.", nameof(id));
        }

        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Assessment name must not be empty.", nameof(name));
        }

        ArgumentNullException.ThrowIfNull(source);

        Id = id;
        TenantId = tenantId;
        Name = name.Trim();
        SourceKind = source.Kind;
        SourceLocator = source.Locator;
        Branch = source.Branch;
        CredentialName = source.CredentialName;
        Status = AssessmentStatus.Created;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public SourceReference Source => new(SourceKind, SourceLocator, Branch, CredentialName, TenantId);

    public IReadOnlyList<string> ExcludeGlobs =>
        string.IsNullOrWhiteSpace(ExcludeGlobsJson) ? [] : System.Text.Json.JsonSerializer.Deserialize<List<string>>(ExcludeGlobsJson) ?? [];

    /// <summary>JSON array of free-form labels ("billing", "client-x") for portfolio grouping and filtering.</summary>
    public string? TagsJson { get; private set; }

    public IReadOnlyList<string> Tags =>
        string.IsNullOrWhiteSpace(TagsJson) ? [] : System.Text.Json.JsonSerializer.Deserialize<List<string>>(TagsJson) ?? [];

    public void SetTags(IEnumerable<string>? tags)
    {
        var list = (tags ?? [])
            .Select(t => t?.Trim() ?? string.Empty)
            .Where(t => t.Length > 0)
            .Select(t => t.Length > 40 ? t[..40] : t)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
        TagsJson = list.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(list);
    }

    public void SetExcludeGlobs(IEnumerable<string>? globs)
    {
        var list = (globs ?? []).Select(g => g.Trim()).Where(g => g.Length > 0 && g.Length <= 500).Distinct(StringComparer.Ordinal).Take(200).ToList();
        ExcludeGlobsJson = list.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(list);
    }

    public void SetSchedule(int? rerunEveryDays, string? webhookUrl)
    {
        if (rerunEveryDays is { } days && (days < 1 || days > 365))
        {
            throw new ArgumentException("Re-run cadence must be between 1 and 365 days.", nameof(rerunEveryDays));
        }

        webhookUrl = string.IsNullOrWhiteSpace(webhookUrl) ? null : webhookUrl.Trim();
        if (webhookUrl is not null && (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")))
        {
            throw new ArgumentException("Webhook must be an absolute http(s) URL.", nameof(webhookUrl));
        }

        RerunEveryDays = rerunEveryDays;
        WebhookUrl = webhookUrl;
    }

    /// <summary>
    /// Points an "upload" assessment at a newly uploaded archive. Only the archive
    /// changes: runs, findings and fingerprints stay attached to this assessment
    /// because <see cref="RepositoryKey"/> for uploads is the assessment itself.
    /// </summary>
    public void ReplaceUpload(string uploadId)
    {
        if (SourceKind != SourceReference.Kinds.Upload)
        {
            throw new InvalidOperationException("Only assessments created from an upload can receive a new upload.");
        }

        if (!Guid.TryParse(uploadId, out var guid))
        {
            throw new ArgumentException("Upload id must be a GUID.", nameof(uploadId));
        }

        SourceLocator = guid.ToString();
    }

    public void SetTarget(int? targetScore, DateTimeOffset? targetDate)
    {
        if (targetScore is { } t && (t < 1 || t > 100))
        {
            throw new ArgumentException("Target score must be between 1 and 100.", nameof(targetScore));
        }

        if (targetScore is null && targetDate is not null)
        {
            throw new ArgumentException("A target date needs a target score.", nameof(targetDate));
        }

        TargetScore = targetScore;
        TargetDate = targetDate;
    }

    public TargetStatus TargetStatusAt(int? score, DateTimeOffset now) => Targets.Evaluate(score, TargetScore, TargetDate, now);

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Assessment name must not be empty.", nameof(name));
        }

        Name = name.Trim();
    }

    /// <summary>
    /// Stable identity of the repository for fingerprinting: provider-neutral,
    /// case-insensitive, without trailing separators or a .git suffix.
    /// </summary>
    public string RepositoryKey => SourceKind == SourceReference.Kinds.Upload ? $"upload:{Id:N}" : NormalizeRepositoryKey(SourceLocator);

    /// <summary>
    /// (Re)starts a run. Running is allowed too: a job whose worker died mid-run is
    /// re-leased after lease expiry and must be able to run again.
    /// </summary>
    public void Start()
    {
        Status = AssessmentStatus.Running;
        StartedAtUtc = DateTimeOffset.UtcNow;
        CompletedAtUtc = null;
        FailureReason = null;
    }

    public void Complete(bool withWarnings)
    {
        EnsureRunning();
        Status = withWarnings ? AssessmentStatus.CompletedWithWarnings : AssessmentStatus.Completed;
        CompletedAtUtc = DateTimeOffset.UtcNow;
    }

    public void Fail(string reason)
    {
        Status = AssessmentStatus.Failed;
        FailureReason = reason;
        CompletedAtUtc = DateTimeOffset.UtcNow;
    }

    public static string NormalizeRepositoryKey(string locator)
    {
        var key = locator.Trim().Replace('\\', '/').TrimEnd('/');
        if (key.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            key = key[..^4];
        }

        return key.ToLowerInvariant();
    }

    private void EnsureRunning()
    {
        if (Status != AssessmentStatus.Running)
        {
            throw new InvalidOperationException($"Assessment {Id} is in state {Status}; expected Running.");
        }
    }
}

public enum AssessmentStatus
{
    Created,
    Running,
    Completed,

    /// <summary>Finished, but at least one scanner failed — the picture is incomplete and the report must say so.</summary>
    CompletedWithWarnings,
    Failed,
}
