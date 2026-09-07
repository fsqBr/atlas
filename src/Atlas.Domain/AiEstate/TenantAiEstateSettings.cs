using System.Text.Json;

namespace Atlas.Domain.AiEstate;

/// <summary>
/// The tenant's approved AI providers — the V0.5 "policy" of the governance module, editable in the UI.
/// An empty list means "no allowlist": the unapproved-provider rule stays silent. Passed to every scan
/// as a setting, so the worker (and its child scan host) never reads settings from the database.
/// </summary>
public sealed class TenantAiEstateSettings
{
    public const int MaxProviders = 100;

    private TenantAiEstateSettings()
    {
    }

    public TenantAiEstateSettings(Guid tenantId, IEnumerable<string> approvedProviders, string updatedBy)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        TenantId = tenantId;
        Update(approvedProviders, updatedBy);
    }

    public Guid TenantId { get; private set; }

    /// <summary>JSON array of provider ids (lower case, catalog ids such as "azure-openai").</summary>
    public string ApprovedProvidersJson { get; private set; } = "[]";

    public string UpdatedBy { get; private set; } = null!;

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public IReadOnlyList<string> ApprovedProviders =>
        JsonSerializer.Deserialize<List<string>>(ApprovedProvidersJson) ?? [];

    public bool HasAllowlist => ApprovedProviders.Count > 0;

    public void Update(IEnumerable<string> approvedProviders, string updatedBy)
    {
        var list = approvedProviders
            .Select(p => p?.Trim().ToLowerInvariant() ?? string.Empty)
            .Where(p => p.Length > 0 && p.Length <= 60 && p.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch is '-' or '.'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        if (list.Count > MaxProviders)
        {
            throw new ArgumentException($"At most {MaxProviders} providers can be approved.", nameof(approvedProviders));
        }

        ApprovedProvidersJson = JsonSerializer.Serialize(list);
        UpdatedBy = string.IsNullOrWhiteSpace(updatedBy) ? "unknown" : updatedBy.Trim();
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>The comma-separated form the scan setting carries.</summary>
    public string ToSetting() => string.Join(",", ApprovedProviders);
}
