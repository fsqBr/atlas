using Atlas.Connector.Git;

namespace Atlas.Connector.Tests;

public class GitHostPolicyTests
{
    private static readonly GitConnectorOptions Restricted = new() { AllowedHosts = ["github.com", "*.visualstudio.com", "dev.azure.com"] };

    [Theory]
    [InlineData("https://github.com/org/repo.git")]
    [InlineData("https://GITHUB.com/org/repo")]
    [InlineData("https://contoso.visualstudio.com/x/_git/y")]
    [InlineData("git@github.com:org/repo.git")]
    [InlineData("file:///sources/atlas")]
    [InlineData("/sources/atlas")]
    public void Allows_listed_hosts_and_local_sources(string locator) => GitHostPolicy.EnsureAllowed(locator, Restricted);

    [Theory]
    [InlineData("https://gitlab.com/org/repo.git")]
    [InlineData("https://evil.github.com.attacker.example/repo")]
    [InlineData("git@bitbucket.org:org/repo.git")]
    public void Refuses_other_hosts(string locator)
    {
        var ex = Assert.Throws<GitHostNotAllowedException>(() => GitHostPolicy.EnsureAllowed(locator, Restricted));
        Assert.Contains("AllowedHosts", ex.Message);
    }

    [Fact]
    public void Empty_list_allows_any_host() => GitHostPolicy.EnsureAllowed("https://gitlab.example.internal/a/b.git", new GitConnectorOptions());

    [Fact]
    public void File_urls_can_be_disabled()
    {
        var options = new GitConnectorOptions { AllowFileUrls = false };
        Assert.Throws<GitHostNotAllowedException>(() => GitHostPolicy.EnsureAllowed("file:///sources/atlas", options));
        Assert.Throws<GitHostNotAllowedException>(() => GitHostPolicy.EnsureAllowed("/sources/atlas", options));
        GitHostPolicy.EnsureAllowed("https://github.com/org/repo.git", options);
    }

    [Fact]
    public void Embedded_credentials_are_refused()
    {
        var ex = Assert.Throws<GitHostNotAllowedException>(() => GitHostPolicy.EnsureAllowed("https://user:token@github.com/org/repo.git", new GitConnectorOptions()));
        Assert.Contains("store a credential", ex.Message);
    }

    [Fact]
    public async Task Connector_refuses_before_running_git()
    {
        var connector = new GitCliConnector(options: new GitConnectorOptions { AllowedHosts = ["github.com"] });
        var target = Path.Combine(Path.GetTempPath(), "atlas-git-" + Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<GitHostNotAllowedException>(() =>
            connector.MaterializeAsync(new Atlas.Domain.Sources.SourceReference("git", "https://gitlab.com/x/y.git"), target, CancellationToken.None));
        Assert.False(Directory.Exists(target));
    }
}
