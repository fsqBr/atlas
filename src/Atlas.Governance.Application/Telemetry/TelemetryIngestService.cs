using System.Text.Json;
using Atlas.Application.Tenants;
using Atlas.Governance.Domain;
using Atlas.Security.Redaction;
using Microsoft.Extensions.Logging;

namespace Atlas.Governance.Application.Telemetry;

public sealed class TelemetryOptions
{
    public const string SectionName = "Atlas:AiEstate:Telemetry";

    /// <summary>pseudonym (default): people appear as otel-…, an HMAC of the identity attribute. label: the attribute itself (e-mail, id).</summary>
    public string ActorMode { get; set; } = "pseudonym";

    /// <summary>Installation key for pseudonyms (Atlas:Secrets:HmacKeyBase64) — the same key the seat pseudonyms use.</summary>
    public string? HmacKeyBase64 { get; set; }
}

public sealed record TelemetryIngestResult(int Accepted, int Ignored, int Streams, IReadOnlyList<string> Notes);

/// <summary>
/// Turns OTLP payloads into usage facts: cumulative counters become increments through the stream table, samples
/// are grouped per (actor, tool, UTC day, model) and added to the day's fact, prices are applied, a live event is
/// recorded per actor/tool touched today, and budgets are evaluated. Each call is one export batch from one sender.
/// </summary>
public sealed class TelemetryIngestService(
    IUsageFactRepository facts,
    ITelemetryStreamRepository streams,
    IUsageEventRepository events,
    IPriceCatalogResolver priceResolver,
    BudgetService budgets,
    IGovernanceUnitOfWork unitOfWork,
    ITenantContext tenant,
    TelemetryOptions options,
    ILogger<TelemetryIngestService> logger)
{
    public Task<TelemetryIngestResult> IngestMetricsAsync(JsonElement root, CancellationToken ct) => IngestAsync(OtlpJsonParser.ParseMetrics(root), ct);

    public Task<TelemetryIngestResult> IngestTracesAsync(JsonElement root, CancellationToken ct) => IngestAsync(OtlpJsonParser.ParseTraces(root), ct);

    public async Task<TelemetryIngestResult> IngestAsync(OtlpParseResult parsed, CancellationToken ct)
    {
        var tenantId = tenant.Require();
        var samples = new List<TelemetrySample>(parsed.Deltas);

        // Cumulative counters: look the streams up in one query, advance them, keep the increments.
        if (parsed.Cumulative.Count > 0)
        {
            var known = (await streams.GetAsync(parsed.Cumulative.Select(c => c.StreamKey).Distinct().ToList(), ct)).ToDictionary(s => s.StreamKey, StringComparer.Ordinal);
            foreach (var point in parsed.Cumulative)
            {
                double delta;
                if (known.TryGetValue(point.StreamKey, out var stream))
                {
                    delta = stream.Advance(point.Value, point.StartTimeUnixNano);
                }
                else
                {
                    stream = new TelemetryStream(tenantId, point.StreamKey, point.Value, point.StartTimeUnixNano);
                    streams.Add(stream);
                    known[point.StreamKey] = stream;
                    delta = point.Value; // first sight of a stream: the counter started at zero when the process started
                }

                if (delta <= 0)
                {
                    continue;
                }

                var t = point.Template;
                var tokens = (long)Math.Round(delta);
                samples.Add(t with
                {
                    InputTokens = t.InputTokens > 0 ? tokens : 0,
                    OutputTokens = t.OutputTokens > 0 ? tokens : 0,
                    CacheReadTokens = t.CacheReadTokens > 0 ? tokens : 0,
                    CacheWriteTokens = t.CacheWriteTokens > 0 ? tokens : 0,
                    ReportedCost = t.ReportedCost is not null ? (decimal)delta : null,
                });
            }

            await streams.PruneBeforeAsync(DateTimeOffset.UtcNow.AddDays(-3), ct);
        }

        if (samples.Count == 0)
        {
            // Nothing moved in this export, but budgets may have been created since the last one: still evaluate.
            await unitOfWork.SaveChangesAsync(ct);
            await budgets.EvaluateAsync(ct);
            return new TelemetryIngestResult(0, parsed.Ignored, parsed.Cumulative.Count, parsed.Notes);
        }

        var prices = await priceResolver.ResolveAsync(ct);
        var hmac = HmacFingerprint.KeyFromBase64(options.HmacKeyBase64);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var groups = samples
            .Select(s => s with { RawActor = Actor(s, hmac) })
            .GroupBy(s => (s.RawActor, s.Tool, Period: DateOnly.FromDateTime(s.At.UtcDateTime), s.Model))
            .ToList();

        var touchedToday = new HashSet<(string Actor, string Tool)>();
        foreach (var g in groups)
        {
            if (g.Key.Period > today.AddDays(1) || g.Key.Period < today.AddDays(-UsageReportService.MaxDaysBack))
            {
                continue;
            }

            var input = g.Sum(s => s.InputTokens);
            var output = g.Sum(s => s.OutputTokens);
            var cacheRead = g.Sum(s => s.CacheReadTokens);
            var cacheWrite = g.Sum(s => s.CacheWriteTokens);
            var requests = g.Sum(s => s.Requests);
            var sessions = g.Where(s => s.SessionId is not null).Select(s => s.SessionId!).Distinct(StringComparer.Ordinal).Count();
            var reportedCost = g.Any(s => s.ReportedCost is not null) ? g.Sum(s => s.ReportedCost ?? 0m) : (decimal?)null;
            var provider = ModelProviders.Normalize(g.Select(s => s.Provider).FirstOrDefault(p => p is not null)) ?? ModelProviders.Guess(g.Key.Model);

            var fact = await facts.GetAsync(g.Key.RawActor, g.Key.Tool, g.Key.Period, g.Key.Model, ct);
            if (fact is null)
            {
                fact = new UsageFact(Guid.NewGuid(), tenantId, g.Key.RawActor, g.Key.Tool, g.Key.Period, g.Key.Model, 0, 0, 0, 0, 0, 0, null, prices.Version, "otel");
                fact.SetProvider(provider);
                facts.Add(fact);
            }

            fact.Add(input, output, cacheRead, cacheWrite, requests, sessions, reportedCost);
            fact.Reprice(prices.Estimate(fact.Model, fact.InputTokens, fact.OutputTokens, fact.CacheReadTokens, fact.CacheWriteTokens), prices.Version);
            if (g.Key.Period == today)
            {
                touchedToday.Add((g.Key.RawActor, g.Key.Tool));
            }
        }

        // Facts first, then the live events from what the database now holds for today (one extra save per batch).
        await unitOfWork.SaveChangesAsync(ct);
        foreach (var (actor, tool) in touchedToday)
        {
            var todays = await facts.ListForActorAsync(actor, tool, today, ct);
            events.Add(new UsageReportEvent(Guid.NewGuid(), tenantId, actor, tool, today, DateTimeOffset.UtcNow,
                todays.Sum(r => r.TotalTokens), todays.All(r => r.EstimatedCost is null) ? null : todays.Sum(r => r.EstimatedCost ?? 0m), todays.Sum(r => r.Requests)));
        }

        await events.PruneBeforeAsync(DateTimeOffset.UtcNow.AddDays(-UsageReportEvent.RetentionDays), ct);
        await unitOfWork.SaveChangesAsync(ct);
        var alerts = await budgets.EvaluateAsync(ct);
        logger.LogInformation("Telemetry batch: {Samples} sample(s) → {Groups} day/model line(s), {Ignored} ignored, {Alerts} alert(s).", samples.Count, groups.Count, parsed.Ignored, alerts);
        return new TelemetryIngestResult(groups.Count, parsed.Ignored, parsed.Cumulative.Count, parsed.Notes);
    }

    /// <summary>People are pseudonymised unless the tenant chose labels; services keep their name.</summary>
    private string Actor(TelemetrySample s, byte[]? hmacKey)
    {
        if (s.ActorKind == ActorKind.Service)
        {
            return Truncate("svc:" + s.RawActor.ToLowerInvariant(), UsageFact.MaxActorLength);
        }

        if (string.Equals(options.ActorMode, "label", StringComparison.OrdinalIgnoreCase))
        {
            return Truncate(s.RawActor, UsageFact.MaxActorLength);
        }

        return "otel-" + HmacFingerprint.Compute(hmacKey, s.RawActor.Trim().ToLowerInvariant())[..12];
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
