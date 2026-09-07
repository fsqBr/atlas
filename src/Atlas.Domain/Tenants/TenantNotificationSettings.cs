namespace Atlas.Domain.Tenants;

/// <summary>
/// A tenant's own notification channels: webhook (signed), Slack/Teams cards and the weekly digest.
/// Overrides the deployment-wide Atlas:Notifications values for that tenant's assessments, and makes
/// the digest safe in multi-tenant installs (each tenant gets only its own numbers, on its own channel).
/// </summary>
public sealed class TenantNotificationSettings
{
    public Guid TenantId { get; private set; }
    public string? WebhookUrl { get; private set; }
    public string? Secret { get; private set; }
    public string? SlackWebhookUrl { get; private set; }
    public string? TeamsWebhookUrl { get; private set; }
    public string? DigestDayOfWeek { get; private set; }
    public int DigestHourUtc { get; private set; } = 13;
    public string UpdatedBy { get; private set; } = null!;
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    private TenantNotificationSettings()
    {
    }

    public TenantNotificationSettings(Guid tenantId, string updatedBy)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        TenantId = tenantId;
        Update(null, null, null, null, null, 13, updatedBy);
    }

    public void Update(
        string? webhookUrl,
        string? secret,
        string? slackWebhookUrl,
        string? teamsWebhookUrl,
        string? digestDayOfWeek,
        int digestHourUtc,
        string updatedBy)
    {
        WebhookUrl = ValidUrlOrNull(webhookUrl);
        Secret = string.IsNullOrWhiteSpace(secret) ? null : secret.Trim();
        SlackWebhookUrl = ValidUrlOrNull(slackWebhookUrl);
        TeamsWebhookUrl = ValidUrlOrNull(teamsWebhookUrl);

        digestDayOfWeek = string.IsNullOrWhiteSpace(digestDayOfWeek) ? null : digestDayOfWeek.Trim();
        if (digestDayOfWeek is not null && !Enum.TryParse<DayOfWeek>(digestDayOfWeek, true, out _))
        {
            throw new ArgumentException("Digest day must be a day of the week (e.g. Monday).", nameof(digestDayOfWeek));
        }

        if (digestHourUtc is < 0 or > 23)
        {
            throw new ArgumentException("Digest hour must be 0–23 (UTC).", nameof(digestHourUtc));
        }

        DigestDayOfWeek = digestDayOfWeek;
        DigestHourUtc = digestHourUtc;
        UpdatedBy = string.IsNullOrWhiteSpace(updatedBy) ? "unknown" : updatedBy.Trim();
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public bool IsEmpty =>
        WebhookUrl is null && Secret is null && SlackWebhookUrl is null && TeamsWebhookUrl is null && DigestDayOfWeek is null;

    private static string? ValidUrlOrNull(string? url)
    {
        url = string.IsNullOrWhiteSpace(url) ? null : url.Trim();
        if (url is not null && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")))
        {
            throw new ArgumentException("Webhook URLs must be absolute http(s) URLs.");
        }

        return url;
    }
}
