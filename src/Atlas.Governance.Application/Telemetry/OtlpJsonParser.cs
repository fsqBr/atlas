using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Atlas.Governance.Application.Telemetry;

/// <summary>Who or what produced the usage: a person (from user.* attributes) or a service (from service.name).</summary>
public enum ActorKind
{
    Person,
    Service,
}

/// <summary>One usage observation after parsing: already an increment, never a cumulative value.</summary>
public sealed record TelemetrySample(
    DateTimeOffset At,
    string RawActor,
    ActorKind ActorKind,
    string Tool,
    string? Provider,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    int Requests,
    decimal? ReportedCost,
    string? SessionId);

/// <summary>A cumulative counter observation the parser cannot turn into an increment on its own.</summary>
public sealed record CumulativePoint(string StreamKey, double Value, long StartTimeUnixNano, TelemetrySample Template);

public sealed record OtlpParseResult(IReadOnlyList<TelemetrySample> Deltas, IReadOnlyList<CumulativePoint> Cumulative, int Ignored, IReadOnlyList<string> Notes);

/// <summary>
/// Reads OTLP/HTTP <b>JSON</b> (ExportMetricsServiceRequest / ExportTraceServiceRequest) and extracts AI usage:
/// Claude Code (<c>claude_code.token.usage</c>, <c>claude_code.cost.usage</c>), Gemini CLI (<c>gemini_cli.token.usage</c>),
/// the OpenTelemetry GenAI semantic conventions (<c>gen_ai.client.token.usage</c> histograms and <c>gen_ai.*</c> spans,
/// which gateways such as LiteLLM and instrumented applications emit). Anything else is counted and ignored.
/// Protobuf payloads are not read here: senders set <c>OTEL_EXPORTER_OTLP_PROTOCOL=http/json</c>.
/// </summary>
public static class OtlpJsonParser
{
    public static OtlpParseResult ParseMetrics(JsonElement root)
    {
        var deltas = new List<TelemetrySample>();
        var cumulative = new List<CumulativePoint>();
        var ignored = 0;
        var notes = new List<string>();
        foreach (var rm in Array(root, "resourceMetrics"))
        {
            var resource = Attributes(rm.TryGetProperty("resource", out var res) ? res : default);
            foreach (var sm in Array(rm, "scopeMetrics"))
            {
                foreach (var metric in Array(sm, "metrics"))
                {
                    var name = Str(metric, "name") ?? "";
                    var kind = name switch
                    {
                        "claude_code.token.usage" => "tokens",
                        "claude_code.cost.usage" => "cost",
                        "gemini_cli.token.usage" => "tokens",
                        "gen_ai.client.token.usage" => "tokens",
                        _ => null,
                    };
                    if (kind is null)
                    {
                        ignored++;
                        continue;
                    }

                    if (metric.TryGetProperty("sum", out var sum))
                    {
                        var isCumulative = Temporality(sum) == 2;
                        foreach (var dp in Array(sum, "dataPoints"))
                        {
                            var attrs = Merge(resource, Attributes(dp));
                            var value = Number(dp);
                            var template = Template(name, kind, attrs, dp, value);
                            if (template is null)
                            {
                                ignored++;
                                continue;
                            }

                            if (isCumulative)
                            {
                                cumulative.Add(new CumulativePoint(StreamKey(name, resource, Attributes(dp)), value, Long(dp, "startTimeUnixNano"), template));
                            }
                            else
                            {
                                deltas.Add(template);
                            }
                        }
                    }
                    else if (metric.TryGetProperty("histogram", out var hist))
                    {
                        // gen_ai.client.token.usage is a histogram of tokens per request: sum = tokens, count = requests.
                        var isCumulative = Temporality(hist) == 2;
                        foreach (var dp in Array(hist, "dataPoints"))
                        {
                            var attrs = Merge(resource, Attributes(dp));
                            var value = dp.TryGetProperty("sum", out var s) ? s.GetDouble() : 0;
                            var count = (int)Long(dp, "count");
                            var template = Template(name, kind, attrs, dp, value, count);
                            if (template is null)
                            {
                                ignored++;
                                continue;
                            }

                            if (isCumulative)
                            {
                                cumulative.Add(new CumulativePoint(StreamKey(name, resource, Attributes(dp)), value, Long(dp, "startTimeUnixNano"), template));
                            }
                            else
                            {
                                deltas.Add(template);
                            }
                        }
                    }
                    else
                    {
                        ignored++;
                        notes.Add($"{name}: only sum and histogram data points are read");
                    }
                }
            }
        }

        return new OtlpParseResult(deltas, cumulative, ignored, notes);
    }

    public static OtlpParseResult ParseTraces(JsonElement root)
    {
        var deltas = new List<TelemetrySample>();
        var ignored = 0;
        foreach (var rs in Array(root, "resourceSpans"))
        {
            var resource = Attributes(rs.TryGetProperty("resource", out var res) ? res : default);
            foreach (var ss in Array(rs, "scopeSpans"))
            {
                foreach (var span in Array(ss, "spans"))
                {
                    var attrs = Merge(resource, Attributes(span));
                    var input = LongAttr(attrs, "gen_ai.usage.input_tokens") ?? LongAttr(attrs, "gen_ai.usage.prompt_tokens") ?? LongAttr(attrs, "llm.usage.prompt_tokens");
                    var output = LongAttr(attrs, "gen_ai.usage.output_tokens") ?? LongAttr(attrs, "gen_ai.usage.completion_tokens") ?? LongAttr(attrs, "llm.usage.completion_tokens");
                    if (input is null && output is null)
                    {
                        ignored++;
                        continue;
                    }

                    var cacheRead = LongAttr(attrs, "gen_ai.usage.cache_read_input_tokens") ?? LongAttr(attrs, "gen_ai.usage.cached_input_tokens") ?? 0;
                    var cacheWrite = LongAttr(attrs, "gen_ai.usage.cache_creation_input_tokens") ?? 0;
                    var model = Get(attrs, "gen_ai.response.model") ?? Get(attrs, "gen_ai.request.model") ?? "unknown";
                    var provider = Get(attrs, "gen_ai.provider.name") ?? Get(attrs, "gen_ai.system");
                    var cost = DecimalAttr(attrs, "gen_ai.usage.cost") ?? DecimalAttr(attrs, "gen_ai.usage.cost_usd");
                    var at = FromUnixNano(Long(span, "startTimeUnixNano")) ?? DateTimeOffset.UtcNow;
                    var (actor, actorKind, tool) = Identity(attrs);
                    deltas.Add(new TelemetrySample(at, actor, actorKind, tool, provider, model, input ?? 0, output ?? 0, cacheRead, cacheWrite, 1, cost, Get(attrs, "session.id")));
                }
            }
        }

        return new OtlpParseResult(deltas, [], ignored, []);
    }

    /// <summary>Builds the sample for one data point; the token type decides which bucket the value lands in.</summary>
    private static TelemetrySample? Template(string metric, string kind, Dictionary<string, string> attrs, JsonElement dp, double value, int requests = 0)
    {
        var at = FromUnixNano(Long(dp, "timeUnixNano")) ?? DateTimeOffset.UtcNow;
        var (actor, actorKind, tool) = Identity(attrs);
        var model = Get(attrs, "model") ?? Get(attrs, "gen_ai.request.model") ?? Get(attrs, "gen_ai.response.model") ?? "unknown";
        var provider = Get(attrs, "gen_ai.provider.name") ?? Get(attrs, "gen_ai.system");
        var session = Get(attrs, "session.id");
        if (kind == "cost")
        {
            return new TelemetrySample(at, actor, actorKind, tool, provider, model, 0, 0, 0, 0, 0, (decimal)value, session);
        }

        var type = (Get(attrs, "type") ?? Get(attrs, "gen_ai.token.type") ?? "input").ToLowerInvariant();
        var tokens = (long)Math.Round(value);
        return type switch
        {
            "input" or "prompt" => new TelemetrySample(at, actor, actorKind, tool, provider, model, tokens, 0, 0, 0, requests, null, session),
            "output" or "completion" or "thought" => new TelemetrySample(at, actor, actorKind, tool, provider, model, 0, tokens, 0, 0, 0, null, session),
            "cacheread" or "cache_read" or "cache" => new TelemetrySample(at, actor, actorKind, tool, provider, model, 0, 0, tokens, 0, 0, null, session),
            "cachecreation" or "cache_creation" or "cache_write" => new TelemetrySample(at, actor, actorKind, tool, provider, model, 0, 0, 0, tokens, 0, null, session),
            "tool" => null, // Gemini's tool tokens are not billed as a separate class; skip rather than misfile
            _ => null,
        };
    }

    /// <summary>Who: user.* attributes make a person; otherwise the service is the actor. The tool is the emitting program.</summary>
    private static (string Actor, ActorKind Kind, string Tool) Identity(Dictionary<string, string> attrs)
    {
        var service = Get(attrs, "service.name") ?? "unknown";
        var tool = service.ToLowerInvariant() switch
        {
            "claude-code" or "claude_code" => "claude-code",
            "gemini-cli" or "gemini_cli" => "gemini-cli",
            var s => s,
        };
        var user = Get(attrs, "user.email") ?? Get(attrs, "user.id") ?? Get(attrs, "user.account_uuid") ?? Get(attrs, "enduser.id") ?? Get(attrs, "user.name");
        return user is not null ? (user, ActorKind.Person, tool) : (service, ActorKind.Service, tool);
    }

    private static string StreamKey(string metric, Dictionary<string, string> resource, Dictionary<string, string> pointAttrs)
    {
        var sb = new StringBuilder(metric);
        foreach (var kv in resource.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            sb.Append('|').Append(kv.Key).Append('=').Append(kv.Value);
        }

        sb.Append("||");
        foreach (var kv in pointAttrs.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            sb.Append('|').Append(kv.Key).Append('=').Append(kv.Value);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    // ---- JSON helpers (proto3 JSON mapping: camelCase, int64 as strings, enums as ints or names) ----

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
        {
            return 0;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var n) ? n : (long)v.GetDouble(),
            JsonValueKind.String => long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0,
            _ => 0,
        };
    }

    private static double Number(JsonElement dp)
    {
        if (dp.TryGetProperty("asInt", out var i))
        {
            return i.ValueKind == JsonValueKind.String ? double.Parse(i.GetString()!, CultureInfo.InvariantCulture) : i.GetDouble();
        }

        return dp.TryGetProperty("asDouble", out var d) ? d.GetDouble() : 0;
    }

    private static int Temporality(JsonElement e)
    {
        if (!e.TryGetProperty("aggregationTemporality", out var t))
        {
            return 2; // OTLP default when absent
        }

        return t.ValueKind switch
        {
            JsonValueKind.Number => t.GetInt32(),
            JsonValueKind.String => t.GetString()!.Contains("DELTA", StringComparison.OrdinalIgnoreCase) ? 1 : 2,
            _ => 2,
        };
    }

    private static Dictionary<string, string> Attributes(JsonElement owner)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in Array(owner, "attributes"))
        {
            var key = Str(kv, "key");
            if (key is null || !kv.TryGetProperty("value", out var v))
            {
                continue;
            }

            var value = v.ValueKind == JsonValueKind.Object
                ? Str(v, "stringValue") ?? (v.TryGetProperty("intValue", out var iv) ? iv.ToString().Trim('"') : null) ?? (v.TryGetProperty("doubleValue", out var dv) ? dv.GetDouble().ToString(CultureInfo.InvariantCulture) : null) ?? (v.TryGetProperty("boolValue", out var bv) ? bv.GetBoolean().ToString() : null)
                : v.ToString();
            if (value is not null)
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static Dictionary<string, string> Merge(Dictionary<string, string> resource, Dictionary<string, string> point)
    {
        var merged = new Dictionary<string, string>(resource, StringComparer.Ordinal);
        foreach (var kv in point)
        {
            merged[kv.Key] = kv.Value;
        }

        return merged;
    }

    private static string? Get(Dictionary<string, string> attrs, string key) => attrs.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    private static long? LongAttr(Dictionary<string, string> attrs, string key) =>
        Get(attrs, key) is { } v && long.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : Get(attrs, key) is { } d && double.TryParse(d, NumberStyles.Any, CultureInfo.InvariantCulture, out var f) ? (long)f : null;

    private static decimal? DecimalAttr(Dictionary<string, string> attrs, string key) =>
        Get(attrs, key) is { } v && decimal.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static DateTimeOffset? FromUnixNano(long nanos) =>
        nanos <= 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(nanos / 1_000_000);
}
