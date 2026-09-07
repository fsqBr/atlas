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
// atlas-agent watch … stay in this terminal and report whenever the logs change (near real time); Ctrl+C stops
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

        if (a.Get("--every") is { } everyText)
        {
            var every = AgentConfig.ParseIntervalMinutes(everyText);
            if (every is null)
            {
                Console.Error.WriteLine($"--every {everyText}: use minutes or hours, e.g. 5m, 30m, 2h, 12 (hours). Minimum 5m.");
                return 2;
            }

            cfg.EveryMinutes = Math.Clamp(every.Value, 5, 7 * 24 * 60);
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
            var how = Scheduler.Install(cfg.EveryMinutes, logPath);
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

    case "watch":
    {
        var cfg = OptionsFromArgs(a, AgentConfig.Load());
        if (string.IsNullOrWhiteSpace(cfg.Server) || string.IsNullOrWhiteSpace(cfg.Token))
        {
            Console.Error.WriteLine("watch needs --server and --token (or ATLAS_SERVER / ATLAS_TOKEN, or a saved install).");
            return 2;
        }

        var interval = TimeSpan.FromMinutes(Math.Clamp(AgentConfig.ParseIntervalMinutes(a.Get("--interval")) ?? 5, 1, 120));
        cfg.Days = Math.Min(cfg.Days, 2); // today and yesterday are enough while watching; the full window came from install/report
        Console.WriteLine($"atlas-agent {version} · watching {string.Join(", ", UsageSources.Select(cfg.Tool).SelectMany(x => x.DefaultRoots()))} · re-sending today at most every {interval.TotalMinutes:0} min and on change · Ctrl+C to stop");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var changed = new SemaphoreSlim(0);
        var watchers = new List<FileSystemWatcher>();
        foreach (var root in UsageSources.Select(cfg.Tool).SelectMany(x => x.DefaultRoots()))
        {
            var w = new FileSystemWatcher(root) { IncludeSubdirectories = true, Filter = "*.jsonl", NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            w.Changed += (_, _) => { if (changed.CurrentCount == 0) changed.Release(); };
            w.Created += (_, _) => { if (changed.CurrentCount == 0) changed.Release(); };
            w.EnableRaisingEvents = true;
            watchers.Add(w);
        }

        var lastSent = DateTimeOffset.MinValue;
        var minGap = TimeSpan.FromSeconds(30); // a session writes many lines; one report per burst is plenty
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var code = await Report(cfg, dryRun: false, quiet: true);
                lastSent = DateTimeOffset.UtcNow;
                if (code == 2)
                {
                    return code; // configuration problem: no point in retrying
                }

                // Wait for a change (debounced) or the interval, whichever comes first.
                var got = await changed.WaitAsync(interval, cts.Token).ConfigureAwait(false);
                if (got)
                {
                    var settle = minGap - (DateTimeOffset.UtcNow - lastSent);
                    if (settle > TimeSpan.Zero)
                    {
                        await Task.Delay(settle, cts.Token);
                    }

                    while (changed.CurrentCount > 0)
                    {
                        await changed.WaitAsync(0, cts.Token);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            foreach (var w in watchers)
            {
                w.Dispose();
            }
        }

        Console.WriteLine("stopped.");
        return 0;
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
            Console.WriteLine($"window:   {cfg.Days} day(s), every {Scheduler.Describe(cfg.EveryMinutes)}, tools: {cfg.Tool}");
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

    var sources = UsageSources.Select(cfg.Tool);
    if (sources.Count == 0)
    {
        Console.Error.WriteLine($"Unknown tool '{cfg.Tool}'. Known: {string.Join(", ", UsageSources.All.Select(x => x.Tool))}, all.");
        return 2;
    }

    var to = DateOnly.FromDateTime(DateTime.UtcNow);
    var from = to.AddDays(-days + 1);
    Say($"atlas-agent {version} · {from:yyyy-MM-dd} → {to:yyyy-MM-dd} · actor: {actor}");

    var reports = new List<(IUsageSource Source, IReadOnlyList<UsageEntryRequest> Entries)>();
    foreach (var source in sources)
    {
        var roots = cfg.Path is not null ? new List<string> { cfg.Path } : source.DefaultRoots().ToList();
        if (roots.Count == 0)
        {
            if (!quiet && sources.Count == 1)
            {
                Console.Error.WriteLine($"No {source.DisplayName} logs found. Use --path <folder> to point at them.");
                return 2;
            }

            continue;
        }

        var entries = source.Read(roots, from, to, out var files);
        Say($"{source.DisplayName}: read {files} transcript file(s) under {string.Join(", ", roots)} → {entries.Count} day/model line(s)");
        if (entries.Count > 0)
        {
            reports.Add((source, entries));
        }
    }

    if (reports.Count == 0)
    {
        if (!quiet)
        {
            Console.WriteLine("Nothing to report for this window.");
            foreach (var (tool, why) in UsageSources.NotReadable)
            {
                Console.WriteLine($"  ({tool}: {why})");
            }
        }

        return 0;
    }

    if (!quiet)
    {
        foreach (var (source, entries) in reports)
        {
            Console.WriteLine();
            Console.WriteLine($"[{source.DisplayName}]");
            Console.WriteLine($"{"date",-12}{"model",-34}{"input",12}{"output",12}{"cache r",12}{"cache w",12}{"req",6}{"sess",6}");
            foreach (var e in entries)
            {
                Console.WriteLine($"{e.Period:yyyy-MM-dd}  {Trunc(e.Model, 32),-34}{e.InputTokens,12:N0}{e.OutputTokens,12:N0}{e.CacheReadTokens,12:N0}{e.CacheWriteTokens,12:N0}{e.Requests,6}{e.Sessions,6}");
            }
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

    using var http = new HttpClient { BaseAddress = new Uri(cfg.Server.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.Token);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("atlas-agent/" + version);
    var exit = 0;
    foreach (var (source, entries) in reports)
    {
        var request = new UsageReportRequest(actor, source.Tool, version, entries);
        try
        {
            using var response = await http.PostAsJsonAsync("api/ai-estate/usage/report", request);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"{source.DisplayName}: Atlas answered HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
                exit = (int)response.StatusCode is 401 or 403 ? 2 : 1;
                continue;
            }

            var result = await response.Content.ReadFromJsonAsync<UsageReportResponse>();
            Say($"{source.DisplayName}: sent. Atlas accepted {result?.Accepted} line(s); estimated cost for the window: {result?.EstimatedCost:N2} {result?.Currency} (price catalog {result?.PriceCatalogVersion}, {result?.UnpricedEntries} unpriced line(s)).");
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
    }

    return exit;

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

    if (a.Get("--tool") is { } tool)
    {
        cfg.Tool = tool.Trim().ToLowerInvariant();
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
    private static readonly HashSet<string> Commands = ["report", "install", "run", "status", "uninstall", "watch"];

    public const string Help = """
        atlas-agent — report your own AI coding-tool usage to Atlas (aggregates only, you stay in control)

          atlas-agent [report] [--server URL] [--token TOKEN] [--actor NAME | --anonymous] [--days N] [--path DIR] [--dry-run]
          atlas-agent install --server URL --token TOKEN [--every 5m|2h|12] [--tool all|claude-code|codex] [--actor NAME | --anonymous] [--days N]
          atlas-agent watch [--interval 5m]        (near real time in this terminal; Ctrl+C to stop)
          atlas-agent status | run | uninstall

          report      read the local logs, print the daily aggregates and send them (default command)
          install     send once to check, save the settings in ~/.atlas-agent/config.json and register a per-user
                      scheduled run (Task Scheduler on Windows, launchd on macOS, crontab on Linux)
          watch       stay running, re-send today whenever the logs change (debounced) or every --interval; the
                      live view in Atlas follows along. Foreground only — nothing survives closing the terminal
          run         what the scheduler calls — report with the saved settings, log to ~/.atlas-agent/agent.log
          status      saved settings (token masked), schedule and last result
          uninstall   remove the schedule and the saved settings

          Tools read: Claude Code (~/.claude/projects), Codex CLI (~/.codex/sessions). Cursor, Copilot and Gemini CLI
          keep no token counts locally, so they cannot be reported from the machine.

          --server    Atlas base URL (or ATLAS_SERVER), e.g. https://atlas.example.com
          --token     API token with the analyst role (or ATLAS_TOKEN); create one in Atlas → Settings → API tokens
          --actor     label shown in Atlas (default: your OS user name)
          --anonymous send a stable per-machine pseudonym instead of a name
          --days      how many days back to read (default 7, max 400)
          --every     interval between scheduled runs: 5m … 7d, e.g. 5m, 30m, 2h, 12 (hours); default 12h (install only)
          --tool      which local tool to read: all (default), claude-code, codex
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
