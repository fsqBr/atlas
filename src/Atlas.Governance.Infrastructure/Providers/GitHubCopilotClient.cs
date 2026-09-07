using System.Net.Http.Headers;
using System.Text.Json;
using Atlas.Governance.Application;
using Atlas.Governance.Domain;
using Atlas.Security.Redaction;
using Microsoft.Extensions.Logging;

namespace Atlas.Governance.Infrastructure.Providers;

/// <summary>Installation key for seat pseudonyms (Atlas:Secrets:HmacKeyBase64) — shared with the secrets scanner.</summary>
public sealed class GovernanceOptions
{
    public string? HmacKeyBase64 { get; set; }
}

/// <summary>
/// GitHub Copilot billing (org token with copilot billing read): seat breakdown and the seat list with
/// last activity. Cost is flat per seat, so the money is an <see cref="CostBasis.Estimated"/> fact from a
/// versioned list price; the insight is idle seats. Seat holders are stored as keyed pseudonyms only.
/// </summary>
public sealed class GitHubCopilotClient(IHttpClientFactory httpClientFactory, GovernanceOptions options, ILogger<GitHubCopilotClient> logger) : ICostProviderClient
{
    public const string PriceCatalogVersion = "copilot-list-2025-09";
    private const int MaxPages = 50;

    /// <summary>USD per seat per month, public list prices at catalog version date.</summary>
    internal static readonly IReadOnlyDictionary<string, decimal> ListPrices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
    {
        ["business"] = 19m,
        ["enterprise"] = 39m,
    };

    public string Provider => CostProviders.GitHubCopilot;

    public async Task<CostCollection> CollectAsync(CostSource source, string secret, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.Scope))
        {
            throw new CostProviderException("github-copilot: the cost source needs the organization login as scope.");
        }

        var org = Uri.EscapeDataString(source.Scope.Trim());
        var http = httpClientFactory.CreateClient(ProviderHttp.HttpClientName);
        var key = HmacFingerprint.KeyFromBase64(options.HmacKeyBase64);

        string? plan = null;
        using (var request = Request($"https://api.github.com/orgs/{org}/copilot/billing", secret))
        using (var document = await ProviderHttp.GetJsonAsync(http, request, Provider, cancellationToken))
        {
            plan = ProviderHttp.Str(document.RootElement, "plan_type");
        }

        var seats = new List<SeatFact>();
        for (var page = 1; page <= MaxPages; page++)
        {
            using var request = Request($"https://api.github.com/orgs/{org}/copilot/billing/seats?per_page=100&page={page}", secret);
            using var document = await ProviderHttp.GetJsonAsync(http, request, Provider, cancellationToken);
            var batch = ParseSeats(document.RootElement, source, key, plan);
            seats.AddRange(batch);
            if (batch.Count < 100)
            {
                break;
            }
        }

        var facts = EstimateMonthlyCost(seats, plan, source, to);
        logger.LogInformation("GitHub Copilot: {Seats} seat(s), plan {Plan}.", seats.Count, plan ?? "unknown");
        return new CostCollection(facts, seats);
    }

    private static HttpRequestMessage Request(string url, string secret)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    /// <summary>seats[] {assignee.login, plan_type, last_activity_at, pending_cancellation_date} → pseudonymous seat facts.</summary>
    internal static IReadOnlyList<SeatFact> ParseSeats(JsonElement root, CostSource source, byte[]? hmacKey, string? orgPlan)
    {
        var seats = new List<SeatFact>();
        foreach (var seat in ProviderHttp.Arr(root, "seats"))
        {
            var assignee = ProviderHttp.Obj(seat, "assignee");
            var login = assignee is null ? null : ProviderHttp.Str(assignee.Value, "login") ?? ProviderHttp.Num(assignee.Value, "id")?.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (login is null)
            {
                continue;
            }

            seats.Add(new SeatFact(
                Guid.NewGuid(), source.TenantId, source.Id, CostProviders.GitHubCopilot,
                HmacFingerprint.Compute(hmacKey, login.ToLowerInvariant()),
                ProviderHttp.Str(seat, "plan_type") ?? orgPlan,
                ProviderHttp.TimeFromIso(seat, "last_activity_at"),
                ProviderHttp.Str(seat, "pending_cancellation_date") is not null));
        }

        return seats;
    }

    /// <summary>Seats × list price for the current month (Estimated basis). Unknown plan → seats counted, money omitted.</summary>
    internal static IReadOnlyList<CostFact> EstimateMonthlyCost(IReadOnlyList<SeatFact> seats, string? orgPlan, CostSource source, DateOnly asOf)
    {
        var monthStart = new DateOnly(asOf.Year, asOf.Month, 1);
        var byPlan = seats.GroupBy(s => (s.Plan ?? orgPlan ?? "unknown").ToLowerInvariant()).OrderBy(g => g.Key, StringComparer.Ordinal);
        var facts = new List<CostFact>();
        foreach (var group in byPlan)
        {
            var price = ListPrices.GetValueOrDefault(group.Key, 0m);
            facts.Add(new CostFact(
                Guid.NewGuid(), source.TenantId, source.Id, CostProviders.GitHubCopilot, CostBasis.Estimated, monthStart,
                "seats", group.Key, price == 0m ? "list price unknown" : "list price", price * group.Count(), "USD", group.Count(), "seats", PriceCatalogVersion));
        }

        return facts;
    }
}
