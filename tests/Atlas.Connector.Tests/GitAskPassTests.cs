using System.Diagnostics;
using Atlas.Connector.Abstractions;
using Atlas.Connector.Git;

namespace Atlas.Connector.Tests;

public class GitAskPassTests
{
    [Fact]
    public void Helper_sets_askpass_and_scoped_variables_and_cleans_up()
    {
        string scriptPath;
        using (var helper = GitCliConnector.AskPassHelper.Create(new ConnectorCredentialValue(null, "s3cret")))
        {
            scriptPath = helper.ScriptPath!;
            Assert.True(File.Exists(scriptPath));
            Assert.True(File.Exists(helper.ConfigPath));
            Assert.Contains("directory = *", File.ReadAllText(helper.ConfigPath));

            var startInfo = new ProcessStartInfo();
            helper.Apply(startInfo);

            Assert.Equal(scriptPath, startInfo.Environment["GIT_ASKPASS"]);
            Assert.Equal(helper.ConfigPath, startInfo.Environment["GIT_CONFIG_GLOBAL"]);
            Assert.Equal("0", startInfo.Environment["GIT_TERMINAL_PROMPT"]);
            Assert.Equal(GitCliConnector.AskPassHelper.DefaultUsername, startInfo.Environment[GitCliConnector.AskPassHelper.UsernameVariable]);
            Assert.Equal("s3cret", startInfo.Environment[GitCliConnector.AskPassHelper.SecretVariable]);
            Assert.Equal("fatal: auth failed for *** (***)", helper.Redact("fatal: auth failed for s3cret (s3cret)"));
        }

        Assert.False(File.Exists(scriptPath));
    }

    [Fact]
    public void Without_credential_only_the_private_config_is_applied()
    {
        using var helper = GitCliConnector.AskPassHelper.Create(null);
        var startInfo = new ProcessStartInfo();
        helper.Apply(startInfo);
        Assert.Null(helper.ScriptPath);
        Assert.False(startInfo.Environment.ContainsKey("GIT_ASKPASS"));
        Assert.False(startInfo.Environment.ContainsKey(GitCliConnector.AskPassHelper.SecretVariable));
        Assert.Equal(helper.ConfigPath, startInfo.Environment["GIT_CONFIG_GLOBAL"]);
        Assert.Equal("untouched", helper.Redact("untouched"));
    }

    [Fact]
    public void Explicit_username_wins_over_default()
    {
        using var helper = GitCliConnector.AskPassHelper.Create(new ConnectorCredentialValue("svc-atlas", "pat"));
        var startInfo = new ProcessStartInfo();
        helper.Apply(startInfo);
        Assert.Equal("svc-atlas", startInfo.Environment[GitCliConnector.AskPassHelper.UsernameVariable]);
    }

    [Fact]
    public void Posix_script_answers_username_and_password_prompts_from_the_environment()
    {
        var script = GitCliConnector.AskPassHelper.BuildScript(windows: false);
        Assert.StartsWith("#!/bin/sh", script);
        Assert.Contains("[Uu]sername*)", script);
        Assert.Contains("$" + GitCliConnector.AskPassHelper.UsernameVariable, script);
        Assert.Contains("$" + GitCliConnector.AskPassHelper.SecretVariable, script);
        Assert.DoesNotContain("\r", script);
    }

    [Fact]
    public async Task Source_with_credential_but_no_provider_fails_clearly()
    {
        var connector = new GitCliConnector();
        var source = new Atlas.Domain.Sources.SourceReference("git", "https://example.invalid/repo.git", CredentialName: "gh");
        var target = Path.Combine(Path.GetTempPath(), "atlas-git-" + Guid.NewGuid().ToString("N"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => connector.MaterializeAsync(source, target, CancellationToken.None));
        Assert.Contains("gh", ex.Message);
        Directory.Delete(target, recursive: true);
    }
}
