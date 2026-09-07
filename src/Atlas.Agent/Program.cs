using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Atlas.Agent;
using Atlas.Governance.Contracts;

// atlas-agent — the developer CLI of the Atlas AI Estate module.
// It reads the usage logs your AI coding tools already keep on THIS machine, shows the daily aggregates it will
// send (tokens, requests, sessions per model — never prompts, paths or project names) and posts them to your
// Atlas so the estate can show usage and estimated cost per developer.
//
// atlas-agent report … one report now (default when no command is given)
// atlas-agent install … remember server/token and schedule `run` with the OS scheduler (Task Scheduler, launchd, cron)
// atlas-agent run … what the scheduler calls: report using the saved config, append to ~/.atlas-agent/agent.log
// atlas-agent status … show the saved config (token masked), the schedule and the last result
// atlas-agent uninstall… remove the schedule and the saved config
//
// The agent is never a service: the OS scheduler starts it, it reports, it exits. The server cannot configure it.

Console.OutputEncoding = Encoding.UTF8;
var a = new Args(args);
if (a.Has("--help") || a.Has("-h"))
{
    Console.WriteLine(Args.Help);
    return 0;
}

var command = a.Command ?? "report";
var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
var logPath = Path.Combine(AgentConfig.Directory, "agent.log");

switch (command)
{
    case "report":
        return await Report(OptionsFromArgs(a, AgentConfig.Load()), a.Has("--dry-run"), quiet: false);

    case "run":
    {
        var cfg = AgentConfig.Load();
        if (cfg is null)
        {
            Console.Error.WriteLine("No saved configuration. Run `atlas-agent install --server … --token …` first.");
            return 2;
        }

        var code = await Report(cfg, dryRun: false, quiet: true);
        cfg.LastRunUtc = DateTimeOffset.UtcNow;
        cfg.LastResult = code == 0 ? "ok" : $"exit {code}";
        cfg.Save();
        return code;
    }

    case "install":
    {
        var cfg = OptionsFromArgs(a, AgentConfig.Load());
        if (string.IsNullOrWhiteSpace(cfg.Server) || string.IsNullOrWhiteSpace(cfg.Token))
        {
            Console.Error.WriteLine("install needs --server and --token (or ATLAS_SERVER / ATLAS_TOKEN).");
            return 2;
        }

        if (int.TryParse(a.Get("--every"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var every))
        {
            cfg.EveryHours = Math.Clamp(every, 1, 168);
        }

        cfg.InstalledAtUtc = DateTimeOffset.UtcNow;
        Console.WriteLine("Checking the server and sending a first report…");
        var first = await Report(cfg, dryRun: false, quiet: false);
        if (first != 0)
        {
            Console.Error.WriteLine("Not installing: the first report failed. Fix the server/token and try again.");
            return first;
        }

        cfg.Save();
        Directory.CreateDirectory(AgentConfig.Directory);
        try
        {
            var how = Scheduler.Install(cfg.EveryHours, logPath);
            Console.WriteLine($"Installed: {how}");
            Console.WriteLine($"Config: {AgentConfig.FilePath} (token stored there — protect this file like any credential). Log: {logPath}");
            Console.WriteLine("Remove any time with `atlas-agent uninstall`.");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Could not register the schedule: {ex.Message}");
            Console.Error.WriteLine("The config was saved; you can schedule `atlas-agent run` yourself.");
            return 1;
        }
    }

    case "uninstall":
    {
        Console.WriteLine(Scheduler.Uninstall());
        if (File.Exists(AgentConfig.FilePath))
        {
            File.Delete(AgentConfig.FilePath);
            Console.WriteLine($"Removed {AgentConfig.FilePath}.");
        }

        return 0;
    }

    case "status":
    {
        var cfg = AgentConfig.Load();
        if (cfg is null)
        {
            Console.WriteLine("Not installed (no saved configuration). Run `atlas-agent install …` or use `atlas-agent report` by hand.");
        }
        else
        {
            Console.WriteLine($"server:   {cfg.Server}");
            Console.WriteLine($"token:    {Mask(cfg.Token)}");
            Console.WriteLine($"actor:    {(cfg.Anonymous ? Pseudonym() + " (anonymous)" : cfg.Actor ?? Environment.UserName)}");
            Console.WriteLine($"window:   {cfg.Days} day(s), every {cfg.EveryHours} h");
            Console.WriteLine($"last run: {(cfg.LastRunUtc is { } t ? $"{t:u} → {cfg.LastResult}" : "never")}");
        }

        Console.WriteLine($"schedule: {Scheduler.Status() ?? "not registered"}");
        return 0;
    }

    default:
        Console.Error.WriteLine($"Unknown command '{command}'.");
        Console.WriteLine(Args.Help);
        return 2;
}

async Task<int> Report(AgentConfig cfg, bool dryRun, bool quiet)
{
    var actor = cfg.Anonymous ? Pseudonym() : (string.IsNullOrWhiteSpace(cfg.Actor) ? Environment.UserName : cfg.Actor);
    var days = Math.Clamp(cfg.Days <= 0 ? 7 : cfg.Days, 1, 400);
    if (cfg.Path is not null && !Directory.Exists(cfg.Path))
    {
        Console.Error.WriteLine($"--path {cfg.Path} does not exist.");
        return 2;
    }

    var roots = cfg.Path is not null ? new List<string> { cfg.Path } : ClaudeCodeUsageReader.DefaultRoots().ToList();
    var to = DateOnly.FromDateTime(DateTime.UtcNow);
    var from = to.AddDays(-days + 1);
    Say($"atlas-agent {version} · Claude Code usage · {from:yyyy-MM-dd} → {to:yyyy-MM-dd} · actor: {actor}");
    if (roots.Count == 0)
    {
        Console.Error.WriteLine("No Claude Code logs found (looked for ~/.claude/projects). Use --path <folder> to point at them.");
        return 2;
    }

    var entries = ClaudeCodeUsageReader.Read(roots, from, to, out var files);
    Say($"read {files} transcript file(s) under {string.Join(", ", roots)}");
    if (entries.Count == 0)
    {
        Say("Nothing to report for this window.");
        return 0;
    }

    if (!quiet)
    {
        Console.WriteLine();
        Console.WriteLine($"{"date",-12}{"model",-34}{"input",12}{"output",12}{"cache r",12}{"cache w",12}{"req",6}{"sess",6}");
        foreach (var e in entries)
        {
            Console.WriteLine($"{e.Period:yyyy-MM-dd}  {Trunc(e.Model, 32),-34}{e.InputTokens,12:N0}{e.OutputTokens,12:N0}{e.CacheReadTokens,12:N0}{e.CacheWriteTokens,12:N0}{e.Requests,6}{e.Sessions,6}");
        }

        Console.WriteLine();
        Console.WriteLine("That is everything that leaves this machine: counts per day and model. No prompts, no file paths, no project names.");
    }

    if (dryRun)
    {
        Console.WriteLine("--dry-run: not sending.");
        return 0;
    }

    if (string.IsNullOrWhiteSpace(cfg.Server) || string.IsNullOrWhiteSpace(cfg.Token))
    {
        Console.Error.WriteLine("Missing --server and/or --token (or ATLAS_SERVER / ATLAS_TOKEN). Create an API token in Atlas → Settings → API tokens (analyst role).");
        return 2;
    }

    var request = new UsageReportRequest(actor, ClaudeCodeUsageReader.Tool, version, entries);
    using var http = new HttpClient { BaseAddress = new Uri(cfg.Server.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.Token);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("atlas-agent/" + version);
    try
    {
        using var response = await http.PostAsJsonAsync("api/ai-estate/usage/report", request);
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"Atlas answered HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            return 1;
        }

        var result = await response.Content.ReadFromJsonAsync<UsageReportResponse>();
        Say($"Sent. Atlas accepted {result?.Accepted} line(s); estimated cost for the window: {result?.EstimatedCost:N2} {result?.Currency} (price catalog {result?.PriceCatalogVersion}, {result?.UnpricedEntries} unpriced line(s)).");
        return 0;
    }
    catch (HttpRequestException ex)
    {
        Console.Error.WriteLine($"Could not reach Atlas at {cfg.Server}: {ex.Message}");
        return 1;
    }
    catch (TaskCanceledException)
    {
        Console.Error.WriteLine($"Timed out reaching Atlas at {cfg.Server}.");
        return 1;
    }

    void Say(string line)
    {
        Console.WriteLine(quiet ? $"{DateTimeOffset.UtcNow:u} {line}" : line);
    }
}

static AgentConfig OptionsFromArgs(Args a, AgentConfig? saved)
{
    var cfg = saved ?? new AgentConfig();
    cfg.Server = a.Get("--server") ?? Environment.GetEnvironmentVariable("ATLAS_SERVER") ?? cfg.Server;
    cfg.Token = a.Get("--token") ?? Environment.GetEnvironmentVariable("ATLAS_TOKEN") ?? cfg.Token;
    if (a.Get("--actor") is { } actor)
    {
        cfg.Actor = actor;
        cfg.Anonymous = false;
    }

    if (a.Has("--anonymous"))
    {
        cfg.Anonymous = true;
        cfg.Actor = null;
    }

    if (int.TryParse(a.Get("--days"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d))
    {
        cfg.Days = Math.Clamp(d, 1, 400);
    }

    if (a.Get("--path") is { } p)
    {
        cfg.Path = p;
    }

    return cfg;
}

static string Trunc(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

static string Mask(string token) => token.Length <= 8 ? "••••" : token[..4] + "…" + token[^4..];

// A stable per-machine pseudonym: nobody at Atlas can turn it back into a person; you can still recognise your own row.
static string Pseudonym()
{
    var seed = $"{Environment.MachineName}|{Environment.UserName}|{Environment.UserDomainName}";
    return "anon-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))[..10];
}

sealed class Args(string[] raw)
{
    private static readonly HashSet<string> Commands = ["report", "install", "run", "status", "uninstall"];

    public const string Help = """
        atlas-agent — report your own AI coding-tool usage to Atlas (aggregates only, you stay in control)

          atlas-agent [report] [--server URL] [--token TOKEN] [--actor NAME | --anonymous] [--days N] [--path DIR] [--dry-run]
          atlas-agent install --server URL --token TOKEN [--every HOURS] [--actor NAME | --anonymous] [--days N]
          atlas-agent status | run | uninstall

          report      read the local logs, print the daily aggregates and send them (default command)
          install     send once to check, save the settings in ~/.atlas-agent/config.json and register a per-user
                      scheduled run (Task Scheduler on Windows, launchd on macOS, crontab on Linux)
          run         what the scheduler calls — report with the saved settings, log to ~/.atlas-agent/agent.log
          status      saved settings (token masked), schedule and last result
          uninstall   remove the schedule and the saved settings

          --server    Atlas base URL (or ATLAS_SERVER), e.g. https://atlas.example.com
          --token     API token with the analyst role (or ATLAS_TOKEN); create one in Atlas → Settings → API tokens
          --actor     label shown in Atlas (default: your OS user name)
          --anonymous send a stable per-machine pseudonym instead of a name
          --days      how many days back to read (default 7, max 400)
          --every     hours between scheduled runs (install only; default 12)
          --path      Claude Code projects folder (default: ~/.claude/projects, or $CLAUDE_CONFIG_DIR/projects)
          --dry-run   print the aggregates and exit without sending

        What is sent: per day and model — input/output/cache tokens, request and session counts. Nothing else.
        """;

    public string? Command => raw.Length > 0 && Commands.Contains(raw[0]) ? raw[0] : null;

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

        var inline = raw.FirstOrDefault(x => x.StartsWith(name + "=", StringComparison.Ordinal));
        return inline?[(name.Length + 1)..];
    }
}
