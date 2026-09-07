using Atlas.Agent;

namespace Atlas.Governance.Tests;

public class AgentCliTests
{
    [Fact]
    public void Config_round_trips_and_never_writes_nulls()
    {
        var cfg = new AgentConfig { Server = "https://atlas.example.com", Token = "atlas_pat_secret", Actor = "ana", Days = 14, EveryHours = 6, InstalledAtUtc = DateTimeOffset.Parse("2026-09-07T00:00:00Z") };
        var json = AgentConfig.Serialize(cfg);
        Assert.DoesNotContain("\"Path\"", json);
        Assert.DoesNotContain("LastRunUtc", json);
        var path = Path.Combine(Path.GetTempPath(), "atlas-agent-" + Guid.NewGuid().ToString("N"), "config.json");
        try
        {
            cfg.Save(path);
            var back = AgentConfig.Load(path);
            Assert.NotNull(back);
            Assert.Equal("ana", back.Actor);
            Assert.Equal(14, back.Days);
            Assert.Equal(6, back.EveryHours);
            Assert.Equal("atlas_pat_secret", back.Token);
            File.WriteAllText(path, "{ not json");
            Assert.Null(AgentConfig.Load(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }

        Assert.Null(AgentConfig.Load(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"), "config.json")));
    }

    [Fact]
    public void Scheduler_builds_per_user_commands_that_call_run_with_no_arguments_from_the_server()
    {
        var exe = OperatingSystem.IsWindows() ? @"C:\Tools\atlas-agent.exe" : "/usr/local/bin/atlas-agent";
        Assert.Equal($"\"{exe}\" run", Scheduler.BuildCommand(exe));

        var hourly = Scheduler.SchtasksCreateArgs(exe, 6);
        Assert.Contains("/SC", hourly);
        Assert.Equal("HOURLY", hourly[Array.IndexOf(hourly, "/SC") + 1]);
        Assert.Equal("6", hourly[Array.IndexOf(hourly, "/MO") + 1]);
        Assert.DoesNotContain("/RU", hourly); // runs as the current user, never SYSTEM
        Assert.DoesNotContain("/RL", hourly); // never HIGHEST
        var daily = Scheduler.SchtasksCreateArgs(exe, 24);
        Assert.Equal("DAILY", daily[Array.IndexOf(daily, "/SC") + 1]);

        var cron = Scheduler.CronLine(exe, 12, "/home/ana/.atlas-agent/agent.log");
        Assert.StartsWith("17 */12 * * * ", cron);
        Assert.EndsWith(Scheduler.CronMarker, cron);
        Assert.StartsWith("17 9 * * * ", Scheduler.CronLine(exe, 48, "/tmp/x.log"));

        var plist = Scheduler.LaunchdPlist(exe, 12, "/Users/ana/.atlas-agent/agent.log");
        Assert.Contains("<integer>43200</integer>", plist);
        Assert.Contains("<string>run</string>", plist);
        Assert.Contains(Scheduler.LaunchdLabel, plist);
    }
}
