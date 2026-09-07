using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Atlas.Application.Assessments;
using Atlas.Governance.Application;
using Microsoft.Extensions.Logging;

namespace Atlas.Governance.Infrastructure;

/// <summary>
/// Delivers budget alerts to the tenant's notification channels (Settings → Administration): the generic webhook
/// (JSON event <c>ai.budget.alert</c>), Slack (blocks) and Teams (Adaptive Card via a Workflows webhook) — the same
/// channels the run notifications and the weekly digest use. Failures are recorded on the alert, never thrown.
/// </summary>
public sealed class WebhookAlertSink(IHttpClientFactory httpClientFactory, ITenantNotificationSettingsRepository settings, ILogger<WebhookAlertSink> logger) : IAlertSink
{
    public const string HttpClientName = "atlas-alerts";

    public async Task<string?> SendAsync(Guid tenantId, string title, string body, AlertChannels? channels, CancellationToken cancellationToken)
    {
        string? webhook = channels?.WebhookUrl, slack = channels?.SlackWebhookUrl, teams = channels?.TeamsWebhookUrl;
        if (channels is null)
        {
            var s = await settings.GetForTenantAsync(tenantId, cancellationToken);
            webhook = s?.WebhookUrl;
            slack = s?.SlackWebhookUrl;
            teams = s?.TeamsWebhookUrl;
        }

        if (string.IsNullOrWhiteSpace(webhook) && string.IsNullOrWhiteSpace(slack) && string.IsNullOrWhiteSpace(teams))
        {
            return "no notification channel configured";
        }

        var http = httpClientFactory.CreateClient(HttpClientName);
        var errors = new List<string>();
        if (!string.IsNullOrWhiteSpace(webhook))
        {
            await Post(http, webhook, new JsonObject { ["event"] = "ai.budget.alert", ["title"] = title, ["message"] = body, ["at"] = DateTimeOffset.UtcNow }, "webhook", errors, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(slack))
        {
            var blocks = new JsonArray(new JsonObject { ["type"] = "section", ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = $":moneybag: *{title}*\n{body}" } });
            await Post(http, slack, new JsonObject { ["text"] = $"{title}: {body}", ["blocks"] = blocks }, "slack", errors, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(teams))
        {
            var card = new JsonObject
            {
                ["type"] = "message",
                ["attachments"] = new JsonArray(new JsonObject
                {
                    ["contentType"] = "application/vnd.microsoft.card.adaptive",
                    ["content"] = new JsonObject
                    {
                        ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                        ["type"] = "AdaptiveCard",
                        ["version"] = "1.4",
                        ["body"] = new JsonArray(
                            new JsonObject { ["type"] = "TextBlock", ["size"] = "Medium", ["weight"] = "Bolder", ["wrap"] = true, ["text"] = title },
                            new JsonObject { ["type"] = "TextBlock", ["wrap"] = true, ["text"] = body }),
                    },
                }),
            };
            await Post(http, teams, card, "teams", errors, cancellationToken);
        }

        return errors.Count == 0 ? null : string.Join("; ", errors);
    }

    private async Task Post(HttpClient http, string url, JsonObject payload, string channel, List<string> errors, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(url, payload, ct);
            if (!response.IsSuccessStatusCode)
            {
                errors.Add($"{channel}: HTTP {(int)response.StatusCode}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            errors.Add($"{channel}: {ex.GetType().Name}");
            logger.LogWarning(ex, "Budget alert delivery to {Channel} failed.", channel);
        }
    }
}
