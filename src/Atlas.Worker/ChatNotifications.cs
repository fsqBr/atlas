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

    private static string DigestHeadline(Atlas.Application.Portfolio.PortfolioDigest d) =>
        $"Atlas weekly digest — average health {d.AverageScore?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "—"}/100{Delta(d.AverageDelta)} · {d.OpenFindings} open{Delta(d.OpenDelta)} · {d.Assessed} assessed";

    private static string DigestBody(Atlas.Application.Portfolio.PortfolioDigest d)
    {
        var lines = new List<string>();
        foreach (var mover in d.Movers)
        {
            lines.Add($"{(mover.To >= mover.From ? "▲" : "▼")} {mover.Name}: {mover.From} → {mover.To}");
        }

        if (d.TargetsAtRisk + d.TargetsMissed > 0)
        {
            lines.Add($"⚑ targets: {d.TargetsAtRisk} at risk, {d.TargetsMissed} missed");
        }

        return lines.Count == 0 ? "No movement this week." : string.Join("\n", lines);
    }

    /// <summary>Slack body for the weekly portfolio digest.</summary>
    public static string DigestSlack(Atlas.Application.Portfolio.PortfolioDigest d) =>
        new JsonObject
        {
            ["text"] = DigestHeadline(d),
            ["blocks"] = new JsonArray(
                new JsonObject { ["type"] = "section", ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = $"*{DigestHeadline(d)}*\n{DigestBody(d)}" } }),
        }.ToJsonString();

    /// <summary>Teams (Workflows webhook) Adaptive Card for the weekly portfolio digest.</summary>
    public static string DigestTeams(Atlas.Application.Portfolio.PortfolioDigest d) =>
        new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray(new JsonObject
            {
                ["contentType"] = "application/vnd.microsoft.card.adaptive",
                ["content"] = new JsonObject
                {
                    ["type"] = "AdaptiveCard",
                    ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                    ["version"] = "1.4",
                    ["body"] = new JsonArray(
                        new JsonObject { ["type"] = "TextBlock", ["size"] = "Medium", ["weight"] = "Bolder", ["wrap"] = true, ["text"] = DigestHeadline(d) },
                        new JsonObject { ["type"] = "TextBlock", ["spacing"] = "None", ["isSubtle"] = true, ["wrap"] = true, ["text"] = DigestBody(d) }),
                },
            }),
        }.ToJsonString();

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
