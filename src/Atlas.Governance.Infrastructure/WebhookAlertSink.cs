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

    public async Task<string?> SendAsync(Guid tenantId, string title, string body, CancellationToken cancellationToken)
    {
        var s = await settings.GetForTenantAsync(tenantId, cancellationToken);
        if (s is null || (string.IsNullOrWhiteSpace(s.WebhookUrl) && string.IsNullOrWhiteSpace(s.SlackWebhookUrl) && string.IsNullOrWhiteSpace(s.TeamsWebhookUrl)))
        {
            return "no notification channel configured";
        }

        var http = httpClientFactory.CreateClient(HttpClientName);
        var errors = new List<string>();
        if (!string.IsNullOrWhiteSpace(s.WebhookUrl))
        {
            await Post(http, s.WebhookUrl, new JsonObject { ["event"] = "ai.budget.alert", ["title"] = title, ["message"] = body, ["at"] = DateTimeOffset.UtcNow }, "webhook", errors, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(s.SlackWebhookUrl))
        {
            var blocks = new JsonArray(new JsonObject { ["type"] = "section", ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = $":moneybag: *{title}*\n{body}" } });
            await Post(http, s.SlackWebhookUrl, new JsonObject { ["text"] = $"{title}: {body}", ["blocks"] = blocks }, "slack", errors, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(s.TeamsWebhookUrl))
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
            await Post(http, s.TeamsWebhookUrl, card, "teams", errors, cancellationToken);
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
