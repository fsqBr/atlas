using System.Text.Json.Nodes;

namespace Atlas.Worker;

/// <summary>
/// Ready-made chat messages for a completed run — Slack (Block Kit) and
/// Microsoft Teams (Adaptive Card via a Workflows webhook). Pure formatting over
/// the same payload the generic webhook receives: scores and counts, never findings.
/// </summary>
public static class ChatNotifications
{
    public static string Delta(int? delta) => delta switch
    {
        null => "",
        > 0 => $" (▲ +{delta})",
        < 0 => $" (▼ {delta})",
        _ => " (=)",
    };

    private static string Headline(RunCompletedPayload p) =>
        $"{p.AssessmentName} — run #{p.RunNumber}: health {(p.HealthScore?.ToString() ?? "—")}/100{Delta(p.HealthDelta)}";

    private static string Facts(RunCompletedPayload p)
    {
        var facts = $"{p.OpenFindings?.ToString() ?? "—"} open · {p.FindingsNew} new · {p.FindingsResolved} resolved · {p.FindingsRegressed} regressed";
        return p.TargetScore is null ? facts : $"{facts} · target {p.TargetScore}: {p.TargetStatus}";
    }

    /// <summary>Slack incoming-webhook body (https://api.slack.com/messaging/webhooks).</summary>
    public static string Slack(RunCompletedPayload p)
    {
        var emoji = p.HealthScore switch { null => "⏳", < 40 => "🔴", < 60 => "🟠", < 80 => "🟡", _ => "🟢" };
        var blocks = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "section",
                ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = $"{emoji} *{Headline(p)}*\n{Facts(p)}" },
            },
        };
        if (p.Url is not null)
        {
            blocks.Add(new JsonObject
            {
                ["type"] = "actions",
                ["elements"] = new JsonArray(new JsonObject
                {
                    ["type"] = "button",
                    ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = "Open in Atlas" },
                    ["url"] = p.Url,
                }),
            });
        }

        return new JsonObject { ["text"] = Headline(p), ["blocks"] = blocks }.ToJsonString();
    }

    /// <summary>Teams message for a Workflows ("post a card in a channel") webhook: an Adaptive Card attachment.</summary>
    public static string Teams(RunCompletedPayload p)
    {
        var body = new JsonArray
        {
            new JsonObject { ["type"] = "TextBlock", ["size"] = "Medium", ["weight"] = "Bolder", ["wrap"] = true, ["text"] = Headline(p) },
            new JsonObject { ["type"] = "TextBlock", ["spacing"] = "None", ["isSubtle"] = true, ["wrap"] = true, ["text"] = Facts(p) },
        };
        var card = new JsonObject
        {
            ["type"] = "AdaptiveCard",
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["version"] = "1.4",
            ["body"] = body,
        };
        if (p.Url is not null)
        {
            card["actions"] = new JsonArray(new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = "Open in Atlas", ["url"] = p.Url });
        }

        return new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray(new JsonObject
            {
                ["contentType"] = "application/vnd.microsoft.card.adaptive",
                ["content"] = card,
            }),
        }.ToJsonString();
    }
}
