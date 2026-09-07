using System.Diagnostics;

namespace Atlas.Agent;

/// <summary>
/// Registers the report with the operating system's own scheduler — a Task Scheduler task on Windows, a launchd
/// agent on macOS, a crontab line on Linux — under the user's account, visible and removable with the OS tools.
/// atlas-agent never runs as a service and never stays resident: the scheduler starts it, it reports, it exits.
/// </summary>
public static class Scheduler
{
    public const string TaskName = "AtlasAgent";
    public const string LaunchdLabel = "com.atlas.agent";
    public const string CronMarker = "# atlas-agent";

    public static string ExecutablePath => Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the executable path.");

    /// <summary>The command the scheduler runs: this binary, `run`, reading the saved config.</summary>
    public static string BuildCommand(string exePath) => $"\"{exePath}\" run";

    /// <summary>launchd plist: StartInterval in seconds, output to ~/.atlas-agent/agent.log.</summary>
    public static string LaunchdPlist(string exePath, int everyHours, string logPath) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key><string>{LaunchdLabel}</string>
          <key>ProgramArguments</key>
          <array><string>{exePath}</string><string>run</string></array>
          <key>StartInterval</key><integer>{Math.Max(1, everyHours) * 3600}</integer>
          <key>RunAtLoad</key><true/>
          <key>StandardOutPath</key><string>{logPath}</string>
          <key>StandardErrorPath</key><string>{logPath}</string>
        </dict>
        </plist>
        """;

    /// <summary>Crontab line: every N hours at minute 17 (spreads load away from :00).</summary>
    public static string CronLine(string exePath, int everyHours, string logPath) =>
        everyHours >= 24
            ? $"17 9 * * * {BuildCommand(exePath)} >> \"{logPath}\" 2>&1 {CronMarker}"
            : $"17 */{Math.Clamp(everyHours, 1, 23)} * * * {BuildCommand(exePath)} >> \"{logPath}\" 2>&1 {CronMarker}";

    /// <summary>schtasks arguments: a per-user task, no elevation, every N hours (daily when N ≥ 24).</summary>
    public static string[] SchtasksCreateArgs(string exePath, int everyHours) =>
        everyHours >= 24
            ? ["/Create", "/F", "/TN", TaskName, "/SC", "DAILY", "/ST", "09:17", "/TR", BuildCommand(exePath)]
            : ["/Create", "/F", "/TN", TaskName, "/SC", "HOURLY", "/MO", Math.Clamp(everyHours, 1, 23).ToString(), "/TR", BuildCommand(exePath)];

    public static string Install(int everyHours, string logPath)
    {
        var exe = ExecutablePath;
        if (OperatingSystem.IsWindows())
        {
            Run("schtasks", SchtasksCreateArgs(exe, everyHours));
            return $"Windows Task Scheduler task '{TaskName}' (every {everyHours} h, runs as {Environment.UserName}). Inspect: schtasks /Query /TN {TaskName}";
        }

        if (OperatingSystem.IsMacOS())
        {
            var plist = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", LaunchdLabel + ".plist");
            Directory.CreateDirectory(Path.GetDirectoryName(plist)!);
            File.WriteAllText(plist, LaunchdPlist(exe, everyHours, logPath));
            TryRun("launchctl", ["unload", plist]);
            Run("launchctl", ["load", plist]);
            return $"launchd agent {LaunchdLabel} ({plist}, every {everyHours} h). Inspect: launchctl list | grep atlas";
        }

        var current = TryRun("crontab", ["-l"]) ?? "";
        var kept = current.Split('\n').Where(l => !l.Contains(CronMarker, StringComparison.Ordinal) && l.Length > 0);
        var next = string.Join('\n', kept.Append(CronLine(exe, everyHours, logPath))) + "\n";
        RunWithInput("crontab", ["-"], next);
        return $"crontab entry (every {everyHours} h, marked '{CronMarker}'). Inspect: crontab -l";
    }

    public static string Uninstall()
    {
        if (OperatingSystem.IsWindows())
        {
            TryRun("schtasks", ["/Delete", "/F", "/TN", TaskName]);
            return $"Task '{TaskName}' removed.";
        }

        if (OperatingSystem.IsMacOS())
        {
            var plist = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", LaunchdLabel + ".plist");
            TryRun("launchctl", ["unload", plist]);
            if (File.Exists(plist))
            {
                File.Delete(plist);
            }

            return $"launchd agent {LaunchdLabel} removed.";
        }

        var current = TryRun("crontab", ["-l"]) ?? "";
        var kept = current.Split('\n').Where(l => !l.Contains(CronMarker, StringComparison.Ordinal) && l.Length > 0).ToList();
        RunWithInput("crontab", ["-"], kept.Count == 0 ? "" : string.Join('\n', kept) + "\n");
        return "crontab entry removed.";
    }

    public static string? Status()
    {
        if (OperatingSystem.IsWindows())
        {
            return TryRun("schtasks", ["/Query", "/TN", TaskName, "/FO", "LIST"]);
        }

        if (OperatingSystem.IsMacOS())
        {
            var plist = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", LaunchdLabel + ".plist");
            return File.Exists(plist) ? $"launchd agent installed: {plist}" : null;
        }

        var current = TryRun("crontab", ["-l"]) ?? "";
        var line = current.Split('\n').FirstOrDefault(l => l.Contains(CronMarker, StringComparison.Ordinal));
        return line is null ? null : "crontab: " + line;
    }

    private static string Run(string file, string[] args)
    {
        var result = TryRun(file, args, out var exit);
        if (exit != 0)
        {
            throw new InvalidOperationException($"{file} {string.Join(' ', args)} failed (exit {exit}): {result}");
        }

        return result ?? "";
    }

    private static string? TryRun(string file, string[] args) => TryRun(file, args, out var exit) is { } s && exit == 0 ? s : null;

    private static string? TryRun(string file, string[] args, out int exitCode)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            exitCode = p.ExitCode;
            return output.Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            exitCode = 127;
            return ex.Message;
        }
    }

    private static void RunWithInput(string file, string[] args, string input)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        p.StandardInput.Write(input);
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"{file} failed (exit {p.ExitCode}): {err}");
        }
    }
}
