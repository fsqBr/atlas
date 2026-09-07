using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Atlas.Governance.Application;
using Atlas.Governance.Domain;
using Atlas.Security.Redaction;
using Microsoft.Extensions.Logging;

namespace Atlas.Governance.Infrastructure.Providers;

/// <summary>
/// Cursor Admin API (Teams/Enterprise plans; key from cursor.com → Settings → Admin API Keys, sent as HTTP Basic
/// user name). Three read-only calls: <c>/teams/members</c> (seats), <c>/teams/spend</c> (billing-cycle spend per
/// member, provider-reported) and <c>/teams/filtered-usage-events</c> (per-request token usage per member → daily
/// usage facts, tool <c>cursor</c>). Members are pseudonymised with the installation HMAC key, the same way telemetry
/// identities are, so a person who also reports through Claude Code lands on the same row.
/// Written against the documented API shapes; a field that is missing is skipped, never guessed.
/// </summary>
public sealed class CursorClient(IHttpClientFactory httpClientFactory, GovernanceOptions options, ILogger<CursorClient> logger) : ICostProviderClient
{
    public const string BaseUrl = "https://api.cursor.com";
    public const string PriceCatalogVersion = "cursor-billing";
    private const int PageSize = 500;
    private const int MaxPages = 40;

    public string Provider => CostProviders.Cursor;

    public async Task<CostCollection> CollectAsync(CostSource source, string secret, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient(ProviderHttp.HttpClientName);
        var key = HmacFingerprint.KeyFromBase64(options.HmacKeyBase64);
        var facts = new List<CostFact>();
        var seats = new List<SeatFact>();
        var usage = new List<UsageFact>();

        // Members → seats (role as plan; activity from the usage events below).
        var members = new Dictionary<string, (string Pseudonym, string Role)>(StringComparer.OrdinalIgnoreCase);
        using (var request = Request(HttpMethod.Get, "/teams/members", secret, null))
        using (var doc = await ProviderHttp.GetJsonAsync(http, request, Provider, cancellationToken))
        {
            foreach (var (email, role) in ParseMembers(doc.RootElement))
            {
                members[email] = (Pseudonym(key, email), role);
            }
        }

        // Spend this billing cycle per member: provider-reported cents.
        var cycleStart = to;
        using (var request = Request(HttpMethod.Post, "/teams/spend", secret, new { page = 1, pageSize = PageSize }))
        using (var doc = await ProviderHttp.GetJsonAsync(http, request, Provider, cancellationToken))
        {
            var (spend, start) = ParseSpend(doc.RootElement);
            cycleStart = start ?? to;
            foreach (var (email, cents) in spend)
            {
                var pseudonym = members.TryGetValue(email, out var m) ? m.Pseudonym : Pseudonym(key, email);
                facts.Add(new CostFact(Guid.NewGuid(), source.TenantId, source.Id, Provider, CostBasis.ProviderReported, to, "member", pseudonym, "cycle spend", cents / 100m, "USD", null, null, PriceCatalogVersion));
            }
        }

        // Usage events → daily token usage per member and model.
        var lastActivity = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var fromMs = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeMilliseconds();
        var toMs = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeMilliseconds() - 1;
        var agg = new Dictionary<(string Actor, DateOnly Period, string Model), long[]>();
        for (var page = 1; page <= MaxPages; page++)
        {
            using var request = Request(HttpMethod.Post, "/teams/filtered-usage-events", secret, new { startDate = fromMs, endDate = toMs, page, pageSize = PageSize });
            using var doc = await ProviderHttp.GetJsonAsync(http, request, Provider, cancellationToken);
            var (events, hasMore) = ParseUsageEvents(doc.RootElement);
            foreach (var e in events)
            {
                var pseudonym = members.TryGetValue(e.Email, out var m) ? m.Pseudonym : Pseudonym(key, e.Email);
                var period = DateOnly.FromDateTime(e.At.UtcDateTime);
                if (period < from || period > to)
                {
                    continue;
                }

                var slot = agg.TryGetValue((pseudonym, period, e.Model), out var s) ? s : agg[(pseudonym, period, e.Model)] = new long[6];
                slot[0] += e.Input;
                slot[1] += e.Output;
                slot[2] += e.CacheRead;
                slot[3] += e.CacheWrite;
                slot[4] += 1;
                slot[5] += e.Cents;
                if (!lastActivity.TryGetValue(pseudonym, out var last) || e.At > last)
                {
                    lastActivity[pseudonym] = e.At;
                }
            }

            if (!hasMore)
            {
                break;
            }
        }

        foreach (var kv in agg)
        {
            var f = new UsageFact(Guid.NewGuid(), source.TenantId, kv.Key.Actor, "cursor", kv.Key.Period, kv.Key.Model, kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3], (int)kv.Value[4], 0, null, PriceCatalogVersion, "cursor-api");
            f.SetProvider(ModelProviders.Guess(kv.Key.Model) ?? "cursor");
            f.Add(0, 0, 0, 0, 0, 0, kv.Value[5] > 0 ? kv.Value[5] / 100m : null);
            usage.Add(f);
        }

        foreach (var (email, m) in members)
        {
            seats.Add(new SeatFact(Guid.NewGuid(), source.TenantId, source.Id, Provider, m.Pseudonym, m.Role, lastActivity.GetValueOrDefault(m.Pseudonym) is { } la && la != default ? la : null, pendingCancellation: false));
        }

        logger.LogInformation("Cursor: {Members} member(s), {Spend} spend row(s), {Usage} usage line(s) since cycle start {Cycle}.", members.Count, facts.Count, usage.Count, cycleStart);
        return new CostCollection(facts, seats) { Usage = usage };
    }

    internal static IReadOnlyList<(string Email, string Role)> ParseMembers(JsonElement root)
    {
        var list = new List<(string, string)>();
        if (root.TryGetProperty("teamMembers", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in arr.EnumerateArray())
            {
                var email = ProviderHttp.Str(m, "email");
                if (!string.IsNullOrWhiteSpace(email))
                {
                    list.Add((email.Trim().ToLowerInvariant(), ProviderHttp.Str(m, "role") ?? "member"));
                }
            }
        }

        return list;
    }

    internal static (IReadOnlyList<(string Email, long Cents)> Spend, DateOnly? CycleStart) ParseSpend(JsonElement root)
    {
        var list = new List<(string, long)>();
        if (root.TryGetProperty("teamMemberSpend", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in arr.EnumerateArray())
            {
                var email = ProviderHttp.Str(m, "email");
                if (string.IsNullOrWhiteSpace(email))
                {
                    continue;
                }

                var cents = m.TryGetProperty("spendCents", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt64() : 0;
                list.Add((email.Trim().ToLowerInvariant(), cents));
            }
        }

        DateOnly? start = null;
        if (root.TryGetProperty("subscriptionCycleStart", out var cs) && cs.ValueKind == JsonValueKind.Number)
        {
            start = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(cs.GetInt64()).UtcDateTime);
        }

        return (list, start);
    }

    internal sealed record UsageEvent(DateTimeOffset At, string Email, string Model, long Input, long Output, long CacheRead, long CacheWrite, long Cents);

    internal static (IReadOnlyList<UsageEvent> Events, bool HasMore) ParseUsageEvents(JsonElement root)
    {
        var list = new List<UsageEvent>();
        if (root.TryGetProperty("usageEvents", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                var email = ProviderHttp.Str(e, "userEmail") ?? ProviderHttp.Str(e, "email");
                if (string.IsNullOrWhiteSpace(email))
                {
                    continue;
                }

                var ts = e.TryGetProperty("timestamp", out var t) ? t.ValueKind == JsonValueKind.Number ? t.GetInt64() : long.TryParse(t.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0 : 0;
                if (ts <= 0)
                {
                    continue;
                }

                var model = ProviderHttp.Str(e, "model") ?? "unknown";
                long input = 0, output = 0, cacheRead = 0, cacheWrite = 0, cents = 0;
                if (e.TryGetProperty("tokenUsage", out var u) && u.ValueKind == JsonValueKind.Object)
                {
                    input = Long(u, "inputTokens");
                    output = Long(u, "outputTokens");
                    cacheRead = Long(u, "cacheReadTokens");
                    cacheWrite = Long(u, "cacheWriteTokens");
                    cents = u.TryGetProperty("totalCents", out var tc) && tc.ValueKind == JsonValueKind.Number ? (long)Math.Round(tc.GetDouble()) : 0;
                }

                list.Add(new UsageEvent(DateTimeOffset.FromUnixTimeMilliseconds(ts), email.Trim().ToLowerInvariant(), model, input, output, cacheRead, cacheWrite, cents));
            }
        }

        var hasMore = root.TryGetProperty("pagination", out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("hasNextPage", out var hn) && hn.ValueKind == JsonValueKind.True;
        return (list, hasMore);
    }

    private static long Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    /// <summary>Same scheme as the telemetry receiver: id-… from the HMAC of the lower-cased e-mail.</summary>
    internal static string Pseudonym(byte[]? key, string email) => "id-" + HmacFingerprint.Compute(key, email.Trim().ToLowerInvariant())[..12];

    private static HttpRequestMessage Request(HttpMethod method, string path, string secret, object? body)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(secret + ":")));
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        return request;
    }
}
