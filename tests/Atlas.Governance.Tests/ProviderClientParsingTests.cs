using System.Text.Json;
using Atlas.Governance.Domain;
using Atlas.Governance.Infrastructure.Providers;

namespace Atlas.Governance.Tests;

/// <summary>The provider payload shapes as documented (2025); parsing is fixture-tested because no test may call a real billing API.</summary>
public class ProviderClientParsingTests
{
    private static readonly CostSource Source = new(Guid.NewGuid(), Guid.NewGuid(), CostProviders.OpenAi, "openai-admin", null);

    [Fact]
    public void OpenAi_costs_buckets_become_project_line_item_facts()
    {
        using var doc = JsonDocument.Parse("""
            { "object": "page", "has_more": false, "next_page": null, "data": [
                { "object": "bucket", "start_time": 1756684800, "end_time": 1756771200, "results": [
                    { "object": "organization.costs.result", "amount": { "value": 12.5, "currency": "usd" }, "line_item": "GPT-4o, input", "project_id": "proj_abc" },
                    { "object": "organization.costs.result", "amount": { "value": 0.25, "currency": "usd" }, "line_item": "Embeddings", "project_id": null },
                    { "object": "organization.costs.result", "amount": { "currency": "usd" }, "line_item": "broken" } ] },
                { "object": "bucket", "start_time": 1756771200, "end_time": 1756857600, "results": [] } ] }
            """);

        var facts = OpenAiCostClient.Parse(doc.RootElement, Source);

        Assert.Equal(2, facts.Count);
        Assert.All(facts, f => Assert.Equal(CostBasis.ProviderReported, f.Basis));
        Assert.All(facts, f => Assert.Equal("USD", f.Currency));
        Assert.Equal(new DateOnly(2025, 9, 1), facts[0].Period);
        Assert.Equal("proj_abc", facts[0].DimensionKey);
        Assert.Equal("GPT-4o, input", facts[0].Detail);
        Assert.Equal(12.5m, facts[0].Amount);
        Assert.Equal("organization", facts[1].DimensionKey);
        Assert.Equal(OpenAiCostClient.ApiVersionLabel, facts[0].PriceCatalogVersion);
    }

    [Fact]
    public void Anthropic_cost_report_amount_strings_become_workspace_facts()
    {
        using var doc = JsonDocument.Parse("""
            { "data": [ { "starting_at": "2025-09-01T00:00:00Z", "ending_at": "2025-09-02T00:00:00Z", "results": [
                { "currency": "USD", "amount": "123.456789", "workspace_id": "wrkspc_1", "description": "Claude Sonnet 4 Usage - Input Tokens", "cost_type": "tokens", "model": "claude-sonnet-4-20250514" },
                { "currency": "USD", "amount": "0.5", "workspace_id": null, "description": null, "cost_type": "web_search" } ] } ],
              "has_more": false, "next_page": null }
            """);

        var facts = AnthropicCostClient.ParseCostReport(doc.RootElement, Source);

        Assert.Equal(2, facts.Count);
        Assert.Equal(123.456789m, facts[0].Amount);
        Assert.Equal("wrkspc_1", facts[0].DimensionKey);
        Assert.Equal("Claude Sonnet 4 Usage - Input Tokens", facts[0].Detail);
        Assert.Equal("default", facts[1].DimensionKey);
        Assert.Equal("web_search", facts[1].Detail);
        Assert.Equal(new DateOnly(2025, 9, 1), facts[1].Period);
    }

    [Fact]
    public void Claude_code_records_fold_to_daily_aggregates_without_any_actor()
    {
        using var doc = JsonDocument.Parse("""
            { "data": [
                { "date": "2025-09-01T00:00:00Z", "actor": { "type": "user_actor", "email_address": "ana@example.test" }, "core_metrics": { "num_sessions": 3, "lines_of_code": { "added": 100, "removed": 20 } },
                  "model_breakdown": [ { "model": "claude-sonnet-4-20250514", "tokens": { "input": 1000, "output": 500, "cache_read": 0, "cache_creation": 0 }, "estimated_cost": { "currency": "USD", "amount": 250 } } ] },
                { "date": "2025-09-01T00:00:00Z", "actor": { "type": "user_actor", "email_address": "bruno@example.test" }, "core_metrics": { "num_sessions": 1 },
                  "model_breakdown": [ { "model": "claude-sonnet-4-20250514", "tokens": { "input": 200, "output": 100 }, "estimated_cost": { "currency": "USD", "amount": 50 } },
                                       { "model": "claude-haiku-4-5", "tokens": { "input": 50, "output": 10 }, "estimated_cost": { "currency": "USD", "amount": 1 } } ] },
                { "date": "2025-09-01T00:00:00Z", "actor": { "type": "user_actor", "email_address": "ana@example.test" }, "core_metrics": { "num_sessions": 2 }, "model_breakdown": [] } ] }
            """);
        var records = doc.RootElement.GetProperty("data").EnumerateArray().ToList();

        var facts = AnthropicCostClient.FoldClaudeCode(records, new DateOnly(2025, 9, 1), Source);

        var developers = Assert.Single(facts, f => f.DimensionKey == "active-developers");
        Assert.Equal(2m, developers.Quantity); // ana counted once
        Assert.Equal(6m, Assert.Single(facts, f => f.DimensionKey == "sessions").Quantity);
        var sonnet = Assert.Single(facts, f => f.DimensionKey == "claude-sonnet-4-20250514");
        Assert.Equal(3.00m, sonnet.Amount); // cents → dollars
        Assert.Equal(1800m, sonnet.Quantity);
        Assert.Equal(CostBasis.Estimated, sonnet.Basis);
        Assert.All(facts, f => Assert.DoesNotContain("example.test", $"{f.DimensionKey} {f.Detail}"));
        Assert.Empty(AnthropicCostClient.FoldClaudeCode([], new DateOnly(2025, 9, 1), Source));
    }

    [Fact]
    public void Copilot_seats_are_pseudonymous_and_priced_by_plan()
    {
        using var doc = JsonDocument.Parse("""
            { "total_seats": 3, "seats": [
                { "assignee": { "login": "octocat", "id": 1 }, "plan_type": "business", "last_activity_at": "2025-09-01T10:00:00Z", "pending_cancellation_date": null },
                { "assignee": { "login": "idle-dev", "id": 2 }, "plan_type": "business", "last_activity_at": "2025-05-01T10:00:00Z", "pending_cancellation_date": "2025-09-30" },
                { "assignee": { "login": "never", "id": 3 }, "plan_type": null, "last_activity_at": null } ] }
            """);
        var source = new CostSource(Guid.NewGuid(), Guid.NewGuid(), CostProviders.GitHubCopilot, "gh-billing", "acme");
        var key = new byte[32];

        var seats = GitHubCopilotClient.ParseSeats(doc.RootElement, source, key, "business");

        Assert.Equal(3, seats.Count);
        Assert.All(seats, s => Assert.Equal(32, s.SeatKey.Length));
        Assert.DoesNotContain(seats, s => s.SeatKey.Contains("octocat", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(GitHubCopilotClient.ParseSeats(doc.RootElement, source, key, "business")[0].SeatKey, seats[0].SeatKey); // stable under the same key
        var now = new DateTimeOffset(2025, 9, 6, 0, 0, 0, TimeSpan.Zero);
        Assert.False(seats[0].IsIdle(now));
        Assert.True(seats[1].IsIdle(now));
        Assert.True(seats[1].PendingCancellation);
        Assert.True(seats[2].IsIdle(now));
        Assert.Equal("business", seats[2].Plan);

        var facts = GitHubCopilotClient.EstimateMonthlyCost(seats, "business", source, new DateOnly(2025, 9, 6));
        var fact = Assert.Single(facts);
        Assert.Equal(CostBasis.Estimated, fact.Basis);
        Assert.Equal(57m, fact.Amount); // 3 × 19
        Assert.Equal(3m, fact.Quantity);
        Assert.Equal(new DateOnly(2025, 9, 1), fact.Period);
        Assert.Equal(GitHubCopilotClient.PriceCatalogVersion, fact.PriceCatalogVersion);
    }
}
