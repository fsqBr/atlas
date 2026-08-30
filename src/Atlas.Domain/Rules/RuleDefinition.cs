using Atlas.Domain.Findings;

namespace Atlas.Domain.Rules;

/// <summary>
/// Catalog entry for a rule a scanner can emit. Findings reference
/// rules by id; the rule's major version participates in the fingerprint, so a
/// breaking change to what a rule means yields new findings instead of silently
/// re-labelling old ones.
/// </summary>
public sealed class RuleDefinition
{
    public string Id { get; private set; } = null!;
    public string ScannerId { get; private set; } = null!;
    public string Version { get; private set; } = null!;
    public FindingCategory Category { get; private set; }
    public Severity DefaultSeverity { get; private set; }
    public string Title { get; private set; } = null!;
    public string Description { get; private set; } = null!;
    public string? Remediation { get; private set; }

    /// <summary>JSON map language → RuleLocalization (titles, descriptions, templates); "{}" when English only.</summary>
    public string LocalizationsJson { get; private set; } = "{}";
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    private RuleDefinition()
    {
    }

    public RuleDefinition(
        string id,
        string scannerId,
        string version,
        FindingCategory category,
        Severity defaultSeverity,
        string title,
        string description,
        string? remediation,
        string localizationsJson = "{}")
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Rule id must not be empty.", nameof(id));
        }

        Id = id;
        ScannerId = scannerId;
        Update(version, category, defaultSeverity, title, description, remediation, localizationsJson);
    }

    public int MajorVersion => FindingFingerprint.MajorVersionOf(Version);

    public void Update(
        string version,
        FindingCategory category,
        Severity defaultSeverity,
        string title,
        string description,
        string? remediation,
        string localizationsJson = "{}")
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("Rule version must not be empty.", nameof(version));
        }

        Version = version;
        Category = category;
        DefaultSeverity = defaultSeverity;
        Title = title;
        Description = description;
        Remediation = remediation;
        LocalizationsJson = string.IsNullOrWhiteSpace(localizationsJson) ? "{}" : localizationsJson;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }
}
