using System.Globalization;
using System.Text.Json;
using Atlas.Governance.Contracts;

namespace Atlas.Agent;

/// <summary>A local AI coding tool whose usage logs the agent can read.</summary>
public interface IUsageSource
{
    /// <summary>Tool id sent to Atlas (claude-code, codex).</summary>
    string Tool { get; }

    string DisplayName { get; }

    /// <summary>Folders that exist on this machine and hold the tool's transcripts.</summary>
    IReadOnlyList<string> DefaultRoots();

    IReadOnlyList<UsageEntryRequest> Read(IEnumerable<string> roots, DateOnly from, DateOnly to, out int filesRead);
}

public static class UsageSources
{
    public static readonly IReadOnlyList<IUsageSource> All = [new ClaudeCodeSource(), new CodexSource()];

    /// <summary>Tools that keep no token counts on disk — named so the user knows why they are absent, not guessed.</summary>
    public static readonly IReadOnlyList<(string Tool, string Why)> NotReadable =
    [
        ("cursor", "Cursor keeps no per-request token counts locally; usage is only in the Cursor dashboard."),
        ("github-copilot", "Copilot keeps no local usage logs; seats and activity come from the GitHub billing API (Atlas cost source)."),
        ("gemini-cli", "Gemini CLI does not persist token usage in its local chat files."),
    ];

    public static IReadOnlyList<IUsageSource> Select(string? tool) =>
        string.IsNullOrWhiteSpace(tool) || tool == "all" ? All : All.Where(s => string.Equals(s.Tool, tool, StringComparison.OrdinalIgnoreCase)).ToList();
}

public sealed class ClaudeCodeSource : IUsageSource
{
    public string Tool => ClaudeCodeUsageReader.Tool;

    public string DisplayName => "Claude Code";

    public IReadOnlyList<string> DefaultRoots() => ClaudeCodeUsageReader.DefaultRoots();

    public IReadOnlyList<UsageEntryRequest> Read(IEnumerable<string> roots, DateOnly from, DateOnly to, out int filesRead) => ClaudeCodeUsageReader.Read(roots, from, to, out filesRead);
}

/// <summary>
/// OpenAI Codex CLI rollouts (<c>~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl</c>, or <c>$CODEX_HOME/sessions</c>).
/// Each <c>event_msg/token_count</c> line carries the usage of the turn that just finished in
/// <c>payload.info.last_token_usage</c>; the model comes from the latest <c>turn_context</c> line of the file.
/// Written against the documented rollout format; a line that does not match is skipped, never guessed.
/// </summary>
public sealed class CodexSource : IUsageSource
{
    public string Tool => "codex";

    public string DisplayName => "Codex CLI";

    public IReadOnlyList<string> DefaultRoots()
    {
        var roots = new List<string>();
        var home = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            roots.Add(Path.Combine(home, "sessions"));
        }

        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions"));
        return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<UsageEntryRequest> Read(IEnumerable<string> roots, DateOnly from, DateOnly to, out int filesRead)
    {
        var agg = new Dictionary<(DateOnly Period, string Model), ClaudeCodeUsageReader.Acc>();
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
                try
                {
                    ReadLines(File.ReadLines(file), Path.GetFileNameWithoutExtension(file), from, to, agg);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        return agg
            .OrderBy(kv => kv.Key.Period).ThenBy(kv => kv.Key.Model, StringComparer.Ordinal)
            .Select(kv => new UsageEntryRequest(kv.Key.Period, kv.Key.Model, kv.Value.Input, kv.Value.Output, kv.Value.CacheRead, kv.Value.CacheWrite, kv.Value.Requests, kv.Value.Sessions.Count))
            .ToList();
    }

    /// <summary>Folds one rollout file; exposed for tests.</summary>
    public static void ReadLines(IEnumerable<string> lines, string sessionFallback, DateOnly from, DateOnly to, Dictionary<(DateOnly Period, string Model), ClaudeCodeUsageReader.Acc> agg)
    {
        var model = "unknown";
        var session = sessionFallback;
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
                var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (type == "session_meta")
                {
                    if (payload.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    {
                        session = id.GetString()!;
                    }

                    continue;
                }

                if (type == "turn_context")
                {
                    if (payload.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(m.GetString()))
                    {
                        model = m.GetString()!;
                    }

                    continue;
                }

                if (type != "event_msg" || !payload.TryGetProperty("type", out var pt) || pt.GetString() != "token_count")
                {
                    continue;
                }

                if (!payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object
                    || !info.TryGetProperty("last_token_usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!root.TryGetProperty("timestamp", out var ts) || !DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
                {
                    continue;
                }

                var period = DateOnly.FromDateTime(when.UtcDateTime);
                if (period < from || period > to)
                {
                    continue;
                }

                var input = Long(usage, "input_tokens");
                var cached = Long(usage, "cached_input_tokens");
                var output = Long(usage, "output_tokens");
                if (input + cached + output == 0)
                {
                    continue;
                }

                var acc = agg.TryGetValue((period, model), out var existing) ? existing : agg[(period, model)] = new ClaudeCodeUsageReader.Acc();
                // Codex counts cached tokens inside input_tokens; keep the split Atlas prices (fresh input vs cache read).
                acc.Input += Math.Max(0, input - cached);
                acc.CacheRead += cached;
                acc.Output += output;
                acc.Requests++;
                acc.Sessions.Add(session);
            }
        }
    }

    private static long Long(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
