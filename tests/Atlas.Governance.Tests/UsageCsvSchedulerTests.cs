using Atlas.Governance.Application;
using Atlas.Governance.Domain;

namespace Atlas.Governance.Tests;

public class UsageCsvSchedulerTests
{
    private static readonly Guid Tenant = Atlas.Domain.Tenants.WellKnownTenants.DefaultId;

    [Fact]
    public void Csv_has_a_bom_a_header_team_resolution_and_formula_safe_cells()
    {
        var team = new Team(Guid.NewGuid(), Tenant, "Data", ["ana"], "t");
        var a = new UsageFact(Guid.NewGuid(), Tenant, "ana", "claude-code", new DateOnly(2026, 9, 7), "claude-sonnet-4-5", 1000, 200, 0, 0, 3, 1, 0.0045m, "list-2026-09", "cli");
        a.SetProvider("anthropic");
        var b = new UsageFact(Guid.NewGuid(), Tenant, "=cmd()|evil", "codex", new DateOnly(2026, 9, 6), "gpt-5, \"quoted\"", 10, 5, 0, 0, 1, 1, null, "list-2026-09", "cli");
        var csv = UsageCsv.Write([a, b], [team]);

        Assert.StartsWith("﻿period,actor,team,tool,provider,model,", csv);
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("2026-09-06,'=cmd()|evil,,codex,openai,\"gpt-5, \"\"quoted\"\"\",10,5,0,0,1,1,,,list-2026-09,cli", lines[1].TrimEnd('\r'));
        Assert.StartsWith("2026-09-07,ana,Data,claude-code,anthropic,claude-sonnet-4-5,1000,200,0,0,3,1,0.0045,,list-2026-09,cli", lines[2].TrimEnd('\r'));
    }

    [Fact]
    public void Team_channels_must_be_https()
    {
        var team = new Team(Guid.NewGuid(), Tenant, "Ops", ["x"], "t");
        Assert.False(team.HasChannels);
        team.SetChannels(null, "https://hooks.slack.com/services/T/B/x", "  ");
        Assert.True(team.HasChannels);
        Assert.Null(team.TeamsWebhookUrl);
        Assert.Throws<ArgumentException>(() => team.SetChannels("http://plain.example/hook", null, null));
        Assert.Throws<ArgumentException>(() => team.SetChannels("not a url", null, null));
    }
}
