using System.Text.Json;
using Atlas.Governance.Contracts;

namespace Atlas.Agent;

/// <summary>
/// Reads the JSONL transcripts Claude Code keeps under <c>~/.claude/projects</c> and folds the assistant
/// messages into daily (period, model) aggregates. Only token counts, request and session counts leave this
/// class — never the messages, file paths or project folders. Streamed chunks that repeat a message id are
/// counted once (same rule as the community tooling).
/// </summary>
public static class ClaudeCodeUsageReader
{
    public const string Tool = "claude-code";

    /// <summary>Default log roots: $CLAUDE_CONFIG_DIR/projects, ~/.claude/projects, ~/.config/claude/projects.</summary>
    public static IReadOnlyList<string> DefaultRoots()
    {
        var roots = new List<string>();
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            roots.Add(Path.Combine(configured, "projects"));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        roots.Add(Path.Combine(home, ".claude", "projects"));
        roots.Add(Path.Combine(home, ".config", "claude", "projects"));
        return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<UsageEntryRequest> Read(IEnumerable<string> roots, DateOnly from, DateOnly to, out int filesRead)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var agg = new Dictionary<(DateOnly Period, string Model), Acc>();
        filesRead = 0;
        foreach (var root in roots)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                filesRead++;
                ReadFile(file, from, to, seen, agg);
            }
        }

        return agg
            .OrderBy(kv => kv.Key.Period).ThenBy(kv => kv.Key.Model, StringComparer.Ordinal)
            .Select(kv => new UsageEntryRequest(kv.Key.Period, kv.Key.Model, kv.Value.Input, kv.Value.Output, kv.Value.CacheRead, kv.Value.CacheWrite, kv.Value.Requests, kv.Value.Sessions.Count))
            .ToList();
    }

    /// <summary>Folds one transcript; exposed for tests.</summary>
    public static void ReadLines(IEnumerable<string> lines, DateOnly from, DateOnly to, HashSet<string> seen, Dictionary<(DateOnly Period, string Model), Acc> agg)
    {
        foreach (var line in lines)
        {
            if (line.Length == 0 || line[0] != '{')
            {
                continue;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var type) || type.GetString() != "assistant")
                {
                    continue;
                }

                if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                    || !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!root.TryGetProperty("timestamp", out var ts) || !DateTimeOffset.TryParse(ts.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var when))
                {
                    continue;
                }

                var period = DateOnly.FromDateTime(when.UtcDateTime);
                if (period < from || period > to)
                {
                    continue;
                }

                var model = message.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "unknown";
                if (model == "<synthetic>")
                {
                    continue;
                }

                // Streaming writes several lines per message: the message id (with the request id) identifies one call.
                var messageId = message.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                var requestId = root.TryGetProperty("requestId", out var rq) && rq.ValueKind == JsonValueKind.String ? rq.GetString() : null;
                var key = messageId is null && requestId is null ? null : $"{messageId}|{requestId}";
                if (key is not null && !seen.Add(key))
                {
                    continue;
                }

                var acc = agg.TryGetValue((period, model), out var existing) ? existing : agg[(period, model)] = new Acc();
                acc.Input += Long(usage, "input_tokens");
                acc.Output += Long(usage, "output_tokens");
                acc.CacheRead += Long(usage, "cache_read_input_tokens");
                acc.CacheWrite += Long(usage, "cache_creation_input_tokens");
                acc.Requests++;
                if (root.TryGetProperty("sessionId", out var sid) && sid.ValueKind == JsonValueKind.String)
                {
                    acc.Sessions.Add(sid.GetString()!);
                }
            }
        }
    }

    private static void ReadFile(string file, DateOnly from, DateOnly to, HashSet<string> seen, Dictionary<(DateOnly, string), Acc> agg)
    {
        try
        {
            ReadLines(File.ReadLines(file), from, to, seen, agg);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static long Long(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    public sealed class Acc
    {
        public long Input;
        public long Output;
        public long CacheRead;
        public long CacheWrite;
        public int Requests;
        public HashSet<string> Sessions { get; } = new(StringComparer.Ordinal);
    }
}
