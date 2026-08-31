using System.Text.Json;
using Atlas.Worker;

namespace Atlas.IntegrationTests;

/// <summary>Pure formatting checks for the Slack / Teams messages built from a completed run.</summary>
public class ChatNotificationTests
{
    private static RunCompletedPayload Payload(int? score = 56, int? delta = 4, string? url = "https://atlas.example.com/assessments/x") =>
        new("run.completed", Guid.NewGuid(), "Legacy Shop", 8, "Completed", score, 52, delta, 48, 3, 1, 0, url, DateTimeOffset.UtcNow, 60, null, "OnTrack");

    [Fact]
    public void Slack_message_carries_headline_facts_and_link()
    {
        var json = ChatNotifications.Slack(Payload());
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Legacy Shop — run #8: health 56/100 (▲ +4)", doc.RootElement.GetProperty("text").GetString());
        var blocks = doc.RootElement.GetProperty("blocks");
        var text = blocks[0].GetProperty("text").GetProperty("text").GetString()!;
        Assert.Contains("🟠", text);
        Assert.Contains("48 open · 3 new · 1 resolved · 0 regressed · target 60: OnTrack", text);
        Assert.Equal("https://atlas.example.com/assessments/x", blocks[1].GetProperty("elements")[0].GetProperty("url").GetString());
    }

    [Fact]
    public void Teams_message_is_an_adaptive_card_and_omits_the_button_without_a_url()
    {
        var json = ChatNotifications.Teams(Payload(url: null));
        using var doc = JsonDocument.Parse(json);
        var card = doc.RootElement.GetProperty("attachments")[0].GetProperty("content");
        Assert.Equal("AdaptiveCard", card.GetProperty("type").GetString());
        Assert.Contains("health 56/100", card.GetProperty("body")[0].GetProperty("text").GetString());
        Assert.False(card.TryGetProperty("actions", out _));
    }

    /// <summary>Root text + first block, parsed (the serializer escapes non-ASCII in the raw JSON).</summary>
    private static string SlackText(RunCompletedPayload payload)
    {
        using var doc = JsonDocument.Parse(ChatNotifications.Slack(payload));
        return doc.RootElement.GetProperty("text").GetString() + " " +
               doc.RootElement.GetProperty("blocks")[0].GetProperty("text").GetProperty("text").GetString();
    }

    [Fact]
    public void Score_bands_and_deltas_read_correctly()
    {
        Assert.Contains("🔴", SlackText(Payload(score: 20)));
        Assert.Contains("🟢", SlackText(Payload(score: 85)));
        Assert.Contains("⏳", SlackText(Payload(score: null)));
        Assert.Contains("(▼ -3)", SlackText(Payload(delta: -3)));
        Assert.DoesNotContain("(", ChatNotifications.Delta(null));
    }
}
