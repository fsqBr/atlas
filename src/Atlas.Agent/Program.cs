using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atlas.Agent;
using Atlas.Governance.Contracts;

// atlas-agent — the opt-in developer CLI of the Atlas AI Estate module.
// It reads the usage logs your AI coding tools already keep on THIS machine, shows you the daily aggregates
// it will send (tokens, requests, sessions per model — never prompts, paths or project names) and posts them
// to your Atlas so the estate can see usage and estimated cost per developer. You run it; nothing runs for you.

Console.OutputEncoding = Encoding.UTF8;
var args_ = new Args(args);
if (args_.Has("--help") || args_.Has("-h"))
{
    Console.WriteLine(Args.Help);
    return 0;
}

var server = args_.Get("--server") ?? Environment.GetEnvironmentVariable("ATLAS_SERVER");
var token = args_.Get("--token") ?? Environment.GetEnvironmentVariable("ATLAS_TOKEN");
var days = int.TryParse(args_.Get("--days"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? Math.Clamp(d, 1, 400) : 7;
var dryRun = args_.Has("--dry-run");
var anonymous = args_.Has("--anonymous");
var actor = args_.Get("--actor") ?? (anonymous ? Pseudonym() : Environment.UserName);

var customPath = args_.Get("--path");
if (customPath is not null && !Directory.Exists(customPath))
{
    Console.Error.WriteLine($"--path {customPath} does not exist.");
    return 2;
}

var roots = customPath is not null ? new List<string> { customPath } : ClaudeCodeUsageReader.DefaultRoots().ToList();
var to = DateOnly.FromDateTime(DateTime.UtcNow);
var from = to.AddDays(-days + 1);

Console.WriteLine($"atlas-agent · Claude Code usage · {from:yyyy-MM-dd} → {to:yyyy-MM-dd} · actor: {actor}");
if (roots.Count == 0)
{
    Console.Error.WriteLine("No Claude Code logs found (looked for ~/.claude/projects). Use --path <folder> to point at them.");
    return 2;
}

var entries = ClaudeCodeUsageReader.Read(roots, from, to, out var files);
Console.WriteLine($"read {files} transcript file(s) under {string.Join(", ", roots)}");
if (entries.Count == 0)
{
    Console.WriteLine("Nothing to report for this window.");
    return 0;
}

Console.WriteLine();
Console.WriteLine($"{"date",-12}{"model",-34}{"input",12}{"output",12}{"cache r",12}{"cache w",12}{"req",6}{"sess",6}");
foreach (var e in entries)
{
    Console.WriteLine($"{e.Period:yyyy-MM-dd}  {Trunc(e.Model, 32),-34}{e.InputTokens,12:N0}{e.OutputTokens,12:N0}{e.CacheReadTokens,12:N0}{e.CacheWriteTokens,12:N0}{e.Requests,6}{e.Sessions,6}");
}

Console.WriteLine();
Console.WriteLine("That is everything that leaves this machine: counts per day and model. No prompts, no file paths, no project names.");

if (dryRun)
{
    Console.WriteLine("--dry-run: not sending.");
    return 0;
}

if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("Missing --server and/or --token (or ATLAS_SERVER / ATLAS_TOKEN). Create an API token in Atlas → Settings → API tokens (analyst role).");
    return 2;
}

var request = new UsageReportRequest(actor, ClaudeCodeUsageReader.Tool, typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0", entries);
using var http = new HttpClient { BaseAddress = new Uri(server.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
http.DefaultRequestHeaders.UserAgent.ParseAdd("atlas-agent/" + request.AgentVersion);
try
{
    using var response = await http.PostAsJsonAsync("api/ai-estate/usage/report", request);
    if (!response.IsSuccessStatusCode)
    {
        var body = await response.Content.ReadAsStringAsync();
        Console.Error.WriteLine($"Atlas answered HTTP {(int)response.StatusCode}: {body}");
        return 1;
    }

    var result = await response.Content.ReadFromJsonAsync<UsageReportResponse>();
    Console.WriteLine($"Sent. Atlas accepted {result?.Accepted} line(s); estimated cost for the window: {result?.EstimatedCost:N2} {result?.Currency} (price catalog {result?.PriceCatalogVersion}, {result?.UnpricedEntries} unpriced line(s)).");
    return 0;
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"Could not reach Atlas at {server}: {ex.Message}");
    return 1;
}

static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

// A stable per-machine pseudonym: nobody at Atlas can turn it back into a person; you can still recognise your own row.
static string Pseudonym()
{
    var seed = $"{Environment.MachineName}|{Environment.UserName}|{Environment.UserDomainName}";
    return "anon-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))[..10];
}

sealed class Args(string[] raw)
{
    public const string Help = """
        atlas-agent — report your own AI coding-tool usage to Atlas (opt-in, aggregates only)

          atlas-agent [--server URL] [--token TOKEN] [--actor NAME | --anonymous] [--days N] [--path DIR] [--dry-run]

          --server    Atlas base URL (or ATLAS_SERVER), e.g. https://atlas.example.com
          --token     API token with the analyst role (or ATLAS_TOKEN); create one in Atlas → Settings → API tokens
          --actor     label shown in Atlas (default: your OS user name)
          --anonymous send a stable per-machine pseudonym instead of a name
          --days      how many days back to read (default 7, max 400)
          --path      Claude Code projects folder (default: ~/.claude/projects, or $CLAUDE_CONFIG_DIR/projects)
          --dry-run   print the aggregates and exit without sending

        What is sent: per day and model — input/output/cache tokens, request and session counts. Nothing else.
        """;

    public bool Has(string flag) => raw.Contains(flag, StringComparer.Ordinal);

    public string? Get(string name)
    {
        for (var i = 0; i < raw.Length - 1; i++)
        {
            if (raw[i] == name)
            {
                return raw[i + 1];
            }
        }

        var inline = raw.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal));
        return inline?[(name.Length + 1)..];
    }
}
