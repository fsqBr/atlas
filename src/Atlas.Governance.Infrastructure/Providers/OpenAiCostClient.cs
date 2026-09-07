using System.Net.Http.Headers;
using System.Text.Json;
using Atlas.Governance.Application;
using Atlas.Governance.Domain;
using Microsoft.Extensions.Logging;

namespace Atlas.Governance.Infrastructure.Providers;

/// <summary>
/// OpenAI organization Costs API (admin key): daily buckets grouped by project and line item.
/// Provider-reported amounts; paginated; read-only.
/// </summary>
public sealed class OpenAiCostClient(IHttpClientFactory httpClientFactory, ILogger<OpenAiCostClient> logger) : ICostProviderClient
{
    public const string ApiVersionLabel = "openai-costs-v1";
    private const string BaseUrl = "https://api.openai.com/v1/organization/costs";
    private const int MaxPages = 20;

    public string Provider => CostProviders.OpenAi;

    public async Task<CostCollection> CollectAsync(CostSource source, string secret, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient(ProviderHttp.HttpClientName);
        var facts = new List<CostFact>();
        string? page = null;
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();

        for (var i = 0; i < MaxPages; i++)
        {
            var url = $"{BaseUrl}?start_time={start}&end_time={end}&bucket_width=1d&group_by=project_id&group_by=line_item&limit=180" + (page is null ? string.Empty : $"&page={Uri.EscapeDataString(page)}");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            using var document = await ProviderHttp.GetJsonAsync(http, request, Provider, cancellationToken);

            facts.AddRange(Parse(document.RootElement, source));
            var hasMore = ProviderHttp.Bool(document.RootElement, "has_more") ?? false;
            page = ProviderHttp.Str(document.RootElement, "next_page");
            if (!hasMore || string.IsNullOrEmpty(page))
            {
                break;
            }
        }

        logger.LogInformation("OpenAI costs: {Count} fact(s) for {From}..{To}.", facts.Count, from, to);
        return new CostCollection(facts, []);
    }

    /// <summary>Buckets → facts. Public shape (2025): data[].results[] with amount.value/currency, project_id, line_item.</summary>
    internal static IReadOnlyList<CostFact> Parse(JsonElement root, CostSource source)
    {
        var facts = new List<CostFact>();
        foreach (var bucket in ProviderHttp.Arr(root, "data"))
        {
            var period = ProviderHttp.DateFromUnix(bucket, "start_time");
            if (period is null)
            {
                continue;
            }

            foreach (var result in ProviderHttp.Arr(bucket, "results"))
            {
                var amount = ProviderHttp.Obj(result, "amount");
                var value = amount is null ? null : ProviderHttp.Num(amount.Value, "value");
                if (value is null)
                {
                    continue;
                }

                var currency = (amount is null ? null : ProviderHttp.Str(amount.Value, "currency")) ?? "USD";
                var project = ProviderHttp.Str(result, "project_id") ?? "organization";
                var lineItem = ProviderHttp.Str(result, "line_item");
                facts.Add(new CostFact(
                    Guid.NewGuid(), source.TenantId, source.Id, CostProviders.OpenAi, CostBasis.ProviderReported, period.Value,
                    "project", project, lineItem, value.Value, currency, null, null, ApiVersionLabel));
            }
        }

        return facts;
    }
}
