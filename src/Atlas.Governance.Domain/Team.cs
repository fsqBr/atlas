using System.Text.Json;
using System.Text.RegularExpressions;

namespace Atlas.Governance.Domain;

/// <summary>
/// A cost centre: the actor labels (developers as they appear in reports, or service names from telemetry) that
/// roll up together. Members are matched case-insensitively; a member may use <c>*</c> as a wildcard
/// (<c>svc-payments-*</c>). Teams are the unit budgets and reports aggregate on — the per-person rows stay what
/// the reporting mechanism produced (no identity mapping beyond what the developer sent).
/// </summary>
public sealed class Team
{
    public const int MaxMembers = 500;

    private Team()
    {
    }

    public Team(Guid id, Guid tenantId, string name, IEnumerable<string> members, string updatedBy)
    {
        Id = id;
        TenantId = tenantId;
        Name = NormalizeName(name);
        Set(members, updatedBy);
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = null!;

    /// <summary>JSON array of member labels (stored as jsonb).</summary>
    public string MembersJson { get; private set; } = "[]";

    public string UpdatedBy { get; private set; } = null!;

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Optional channels for this team's budget alerts; when all are empty the tenant's channels are used.</summary>
    public string? WebhookUrl { get; private set; }

    public string? SlackWebhookUrl { get; private set; }

    public string? TeamsWebhookUrl { get; private set; }

    public bool HasChannels => !string.IsNullOrWhiteSpace(WebhookUrl) || !string.IsNullOrWhiteSpace(SlackWebhookUrl) || !string.IsNullOrWhiteSpace(TeamsWebhookUrl);

    public void SetChannels(string? webhookUrl, string? slackWebhookUrl, string? teamsWebhookUrl)
    {
        WebhookUrl = Url(webhookUrl);
        SlackWebhookUrl = Url(slackWebhookUrl);
        TeamsWebhookUrl = Url(teamsWebhookUrl);
    }

    private static string? Url(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var v = value.Trim();
        if (v.Length > 1000 || !Uri.TryCreate(v, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Notification channels must be https URLs.");
        }

        return v;
    }

    public IReadOnlyList<string> Members => JsonSerializer.Deserialize<List<string>>(MembersJson) ?? [];

    public void Rename(string name) => Name = NormalizeName(name);

    public void Set(IEnumerable<string> members, string updatedBy)
    {
        var list = members
            .Select(m => (m ?? "").Trim())
            .Where(m => m.Length > 0)
            .Select(m => m.Length <= 120 ? m : m[..120])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (list.Count > MaxMembers)
        {
            throw new ArgumentException($"A team has at most {MaxMembers} members.");
        }

        MembersJson = JsonSerializer.Serialize(list);
        UpdatedBy = string.IsNullOrWhiteSpace(updatedBy) ? "web" : updatedBy.Trim()[..Math.Min(updatedBy.Trim().Length, 100)];
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public bool Matches(string actor)
    {
        foreach (var m in Members)
        {
            if (m.Contains('*'))
            {
                var pattern = "^" + Regex.Escape(m).Replace("\\*", ".*") + "$";
                if (Regex.IsMatch(actor, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)))
                {
                    return true;
                }
            }
            else if (string.Equals(m, actor, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A team needs a name.", nameof(name));
        }

        var n = name.Trim();
        return n.Length <= 80 ? n : n[..80];
    }
}

/// <summary>
/// The last value seen for one cumulative OpenTelemetry counter stream, so cumulative exports turn into the
/// per-interval increments the usage facts need. A stream is one metric with one attribute set from one resource.
/// </summary>
public sealed class TelemetryStream
{
    private TelemetryStream()
    {
    }

    public TelemetryStream(Guid tenantId, string streamKey, double lastValue, long startTimeUnixNano)
    {
        TenantId = tenantId;
        StreamKey = streamKey;
        LastValue = lastValue;
        StartTimeUnixNano = startTimeUnixNano;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid TenantId { get; private set; }

    /// <summary>SHA-256 of metric name + resource identity + sorted attributes (hex).</summary>
    public string StreamKey { get; private set; } = null!;

    public double LastValue { get; private set; }

    public long StartTimeUnixNano { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Returns the increment this observation represents and remembers it. A lower value or a new start time is a reset.</summary>
    public double Advance(double value, long startTimeUnixNano)
    {
        var delta = value < LastValue || (startTimeUnixNano != 0 && startTimeUnixNano != StartTimeUnixNano) ? value : value - LastValue;
        LastValue = value;
        StartTimeUnixNano = startTimeUnixNano;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
        return Math.Max(0, delta);
    }
}
