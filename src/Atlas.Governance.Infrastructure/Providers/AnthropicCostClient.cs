using System.Globalization;
using System.Text.Json;
using Atlas.Governance.Application;
using Atlas.Governance.Domain;
using Microsoft.Extensions.Logging;

namespace Atlas.Governance.Infrastructure.Providers;

/// <summary>
/// Anthropic Admin API (admin key): the organization cost report (provider-reported, per workspace and
/// description) and the Claude Code analytics report (estimated cost per model, plus activity). The
/// analytics endpoint is per developer at the source; this client folds it to per-day aggregates before
/// anything is stored — no actor identifier ever leaves this method.
/// </summary>
public sealed class AnthropicCostClient(IHttpClientFactory httpClientFactory, ILogger<AnthropicCostClient> logger) : ICostProviderClient
{
    public const string ApiVersionLabel = "anthropic-admin-2023-06-01";
    public const string ClaudeCodeLabel = "anthropic-claude-code-analytics-v1";
    private const string CostReportUrl = "https://api.anthropic.com/v1/organizations/cost_report";
    private const string ClaudeCodeUrl = "https://api.anthropic.com/v1/organizations/usage_report/claude_code";
    private const int MaxPages = 20;
    private const int ClaudeCodeDays = 14;

    public string Provider => CostProviders.Anthropic;

    public async Task<CostCollection> CollectAsync(CostSource source, string secret, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient(ProviderHttp.HttpClientName);
        var facts = new List<CostFact>();

        // 1. Cost report: daily buckets, grouped by workspace and description.
        string? page = null;
        var startingAt = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00Z";
        var endingAt = to.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00Z";
        for (var i = 0; i < MaxPages; i++)
        {
            var url = $"{CostReportUrl}?starting_at={startingAt}&ending_at={endingAt}&bucket_width=1d&group_by[]=workspace_id&group_by[]=description&limit=31" + (page is null ? string.Empty : $"&page={Uri.EscapeDataString(page)}");
            using var request = Request(url, secret);
            using var document = await ProviderHttp.GetJsonAsync(http, request, Provider, cancellationToken);
            facts.AddRange(ParseCostReport(document.RootElement, source));
            var hasMore = ProviderHttp.Bool(document.RootElement, "has_more") ?? false;
            page = ProviderHttp.Str(document.RootElement, "next_page");
            if (!hasMore || string.IsNullOrEmpty(page))
            {
                break;
            }
        }

        // 2. Claude Code analytics: one call per day for the recent window; absent for accounts without Claude Code.
        var ccFrom = to.AddDays(-Math.Min(ClaudeCodeDays, to.DayNumber - from.DayNumber));
        for (var day = ccFrom; day <= to; day = day.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                facts.AddRange(await CollectClaudeCodeDayAsync(http, source, secret, day, cancellationToken));
            }
            catch (CostProviderException ex) when (ex.Message.Contains("404", StringComparison.Ordinal) || ex.Message.Contains("403", StringComparison.Ordinal))
            {
                logger.LogInformation("Anthropic Claude Code analytics not available for this organization; skipping ({Error}).", ex.Message);
                break;
            }
        }

        logger.LogInformation("Anthropic costs: {Count} fact(s) for {From}..{To}.", facts.Count, from, to);
        return new CostCollection(facts, []);
    }

    private async Task<IReadOnlyList<CostFact>> CollectClaudeCodeDayAsync(HttpClient http, CostSource source, string secret, DateOnly day, CancellationToken cancellationToken)
    {
        var records = new List<JsonElement>();
        var documents = new List<JsonDocument>();
        try
        {
            string? page = null;
            for (var i = 0; i < MaxPages; i++)
            {
                var url = $"{ClaudeCodeUrl}?starting_at={day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}&limit=1000" + (page is null ? string.Empty : $"&page={Uri.EscapeDataString(page)}");
                using var request = Request(url, secret);
                var document = await ProviderHttp.GetJsonAsync(http, request, Provider, cancellationToken);
                documents.Add(document);
                records.AddRange(ProviderHttp.Arr(document.RootElement, "data"));
                var hasMore = ProviderHttp.Bool(document.RootElement, "has_more") ?? false;
                page = ProviderHttp.Str(document.RootElement, "next_page");
                if (!hasMore || string.IsNullOrEmpty(page))
                {
                    break;
                }
            }

            return FoldClaudeCode(records, day, source);
        }
        finally
        {
            foreach (var d in documents)
            {
                d.Dispose();
            }
        }
    }

    private static HttpRequestMessage Request(string url, string secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("x-api-key", secret);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        return request;
    }

    /// <summary>cost_report: data[] buckets (starting_at) with results[] {amount (string, USD), currency, workspace_id, description, cost_type, model}.</summary>
    internal static IReadOnlyList<CostFact> ParseCostReport(JsonElement root, CostSource source)
    {
        var facts = new List<CostFact>();
        foreach (var bucket in ProviderHttp.Arr(root, "data"))
        {
            var period = ProviderHttp.DateFromIso(bucket, "starting_at");
            if (period is null)
            {
                continue;
            }

            foreach (var result in ProviderHttp.Arr(bucket, "results"))
            {
                var amount = ProviderHttp.Num(result, "amount");
                if (amount is null)
                {
                    continue;
                }

                var workspace = ProviderHttp.Str(result, "workspace_id") ?? "default";
                var description = ProviderHttp.Str(result, "description") ?? ProviderHttp.Str(result, "model") ?? ProviderHttp.Str(result, "cost_type");
                facts.Add(new CostFact(
                    Guid.NewGuid(), source.TenantId, source.Id, CostProviders.Anthropic, CostBasis.ProviderReported, period.Value,
                    "workspace", workspace, description, amount.Value, ProviderHttp.Str(result, "currency") ?? "USD", null, null, ApiVersionLabel));
            }
        }

        return facts;
    }

    /// <summary>
    /// Claude Code analytics records (one per actor per day) → per-day aggregates only: distinct active
    /// developers, sessions, and estimated cost + tokens per model. Actor identifiers are read only to count.
    /// </summary>
    internal static IReadOnlyList<CostFact> FoldClaudeCode(IReadOnlyList<JsonElement> records, DateOnly day, CostSource source)
    {
        if (records.Count == 0)
        {
            return [];
        }

        var actors = new HashSet<string>(StringComparer.Ordinal);
        decimal sessions = 0;
        var perModel = new Dictionary<string, (decimal Cost, decimal Tokens, string Currency)>(StringComparer.Ordinal);

        foreach (var record in records)
        {
            var actor = ProviderHttp.Obj(record, "actor");
            var actorKey = actor is null ? null : ProviderHttp.Str(actor.Value, "email_address") ?? ProviderHttp.Str(actor.Value, "api_key_name") ?? ProviderHttp.Str(actor.Value, "id");
            if (actorKey is not null)
            {
                actors.Add(actorKey);
            }

            var core = ProviderHttp.Obj(record, "core_metrics");
            sessions += core is null ? 0 : ProviderHttp.Num(core.Value, "num_sessions") ?? 0;

            foreach (var model in ProviderHttp.Arr(record, "model_breakdown"))
            {
                var name = ProviderHttp.Str(model, "model") ?? "unknown";
                var cost = ProviderHttp.Obj(model, "estimated_cost");
                var cents = cost is null ? null : ProviderHttp.Num(cost.Value, "amount");
                var currency = (cost is null ? null : ProviderHttp.Str(cost.Value, "currency")) ?? "USD";
                var tokens = ProviderHttp.Obj(model, "tokens");
                var tokenTotal = tokens is null ? 0 : (ProviderHttp.Num(tokens.Value, "input") ?? 0) + (ProviderHttp.Num(tokens.Value, "output") ?? 0) + (ProviderHttp.Num(tokens.Value, "cache_read") ?? 0) + (ProviderHttp.Num(tokens.Value, "cache_creation") ?? 0);
                var current = perModel.GetValueOrDefault(name, (0, 0, currency));
                perModel[name] = (current.Cost + (cents ?? 0) / 100m, current.Tokens + tokenTotal, currency);
            }
        }

        var facts = new List<CostFact>
        {
            new(Guid.NewGuid(), source.TenantId, source.Id, CostProviders.Anthropic, CostBasis.Estimated, day, "claude-code", "active-developers", null, 0m, "USD", actors.Count, "developers", ClaudeCodeLabel),
            new(Guid.NewGuid(), source.TenantId, source.Id, CostProviders.Anthropic, CostBasis.Estimated, day, "claude-code", "sessions", null, 0m, "USD", sessions, "sessions", ClaudeCodeLabel),
        };
        facts.AddRange(perModel.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new CostFact(
            Guid.NewGuid(), source.TenantId, source.Id, CostProviders.Anthropic, CostBasis.Estimated, day, "claude-code", kv.Key, "estimated", kv.Value.Cost, kv.Value.Currency, kv.Value.Tokens, "tokens", ClaudeCodeLabel)));
        return facts;
    }
}
