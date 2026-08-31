using System.Net;
using Atlas.Connector.GitLab;
using Atlas.Domain.Sources;

namespace Atlas.Connector.Tests;

public class GitLabConnectorTests
{
    private static string Project(string path, string name, bool archived = false, string visibility = "private") =>
        $$"""{"id":1,"name":"{{name}}","path_with_namespace":"{{path}}","default_branch":"main","archived":{{(archived ? "true" : "false")}},"last_activity_at":"2026-08-10T08:00:00Z","visibility":"{{visibility}}"}""";

    [Theory]
    [InlineData("acme", "acme")]
    [InlineData("acme/platform/billing-api", "acme/platform/billing-api")]
    [InlineData("https://gitlab.com/acme/platform/billing-api.git", "acme/platform/billing-api")]
    [InlineData("https://gitlab.com/acme/platform/billing-api/-/tree/main", "acme/platform/billing-api")]
    public void Parses_locators(string locator, string expected) => Assert.Equal(expected, GitLabLocator.Parse(locator));

    [Theory]
    [InlineData("")]
    [InlineData("a/../b")]
    [InlineData("bad path/x")]
    public void Rejects_bad_locators(string locator) => Assert.Throws<ArgumentException>(() => GitLabLocator.Parse(locator));

    [Fact]
    public async Task Path_that_is_a_project_is_returned_directly()
    {
        var http = new FakeHttp(_ => FakeHttp.Json(Project("acme/billing-api", "Billing API", visibility: "public")));
        var connector = new GitLabConnector(http, new FakeCloner(), new GitLabConnectorOptions(), new FakeCredentials(null, "glpat-x"));

        var repos = await connector.DiscoverRepositoriesAsync(new SourceReference("gitlab", "acme/billing-api", CredentialName: "gh"), CancellationToken.None);

        var request = Assert.Single(http.Requests);
        Assert.Equal("https://gitlab.com/api/v4/projects/acme%2Fbilling-api", request.RequestUri!.ToString());
        Assert.Equal("glpat-x", request.Headers.GetValues("PRIVATE-TOKEN").Single());
        var repo = Assert.Single(repos);
        Assert.Equal(("Billing API", "acme/billing-api", "main", false), (repo.Name, repo.Locator, repo.DefaultBranch, repo.IsPrivate));
    }

    [Fact]
    public async Task Path_that_is_a_group_lists_projects_including_subgroups()
    {
        var http = new FakeHttp(request => request.RequestUri!.AbsolutePath.StartsWith("/api/v4/projects/")
            ? FakeHttp.Json("""{"message":"404 Project Not Found"}""", HttpStatusCode.NotFound)
            : FakeHttp.Json("[" + Project("acme/billing-api", "billing-api") + "," + Project("acme/platform/legacy", "legacy", archived: true) + "]"));
        var connector = new GitLabConnector(http, new FakeCloner(), new GitLabConnectorOptions());

        var repos = await connector.DiscoverRepositoriesAsync(new SourceReference("gitlab", "acme"), CancellationToken.None);

        Assert.Equal(2, repos.Count);
        Assert.Contains("include_subgroups=true", http.Requests[^1].RequestUri!.Query);
        Assert.StartsWith("/api/v4/groups/acme/projects", http.Requests[^1].RequestUri!.AbsolutePath);
        Assert.True(repos.Single(r => r.Name == "legacy").Archived);
    }

    [Fact]
    public async Task Falls_back_to_user_projects_for_single_segment_paths()
    {
        var http = new FakeHttp(request => request.RequestUri!.AbsolutePath.StartsWith("/api/v4/users/")
            ? FakeHttp.Json("[" + Project("jane/dotfiles", "dotfiles") + "]")
            : FakeHttp.Json("""{"message":"404 Not Found"}""", HttpStatusCode.NotFound));
        var connector = new GitLabConnector(http, new FakeCloner(), new GitLabConnectorOptions());

        var repos = await connector.DiscoverRepositoriesAsync(new SourceReference("gitlab", "jane"), CancellationToken.None);

        Assert.Single(repos);
        Assert.Equal(3, http.Requests.Count); // project, group, user
    }

    [Fact]
    public async Task Materializes_with_the_instance_base_url()
    {
        var cloner = new FakeCloner();
        var connector = new GitLabConnector(new FakeHttp(_ => throw new InvalidOperationException()), cloner, new GitLabConnectorOptions { BaseUrl = "https://git.example.com/" });

        await connector.MaterializeAsync(new SourceReference("gitlab", "acme/platform/billing-api", "develop", "gl"), "/tmp/x", CancellationToken.None);

        Assert.Equal(new SourceReference("git", "https://git.example.com/acme/platform/billing-api.git", "develop", "gl"), cloner.Last);
    }

    [Fact]
    public async Task Group_path_cannot_be_materialized()
    {
        var connector = new GitLabConnector(new FakeHttp(_ => throw new InvalidOperationException()), new FakeCloner(), new GitLabConnectorOptions());
        await Assert.ThrowsAsync<InvalidOperationException>(() => connector.MaterializeAsync(new SourceReference("gitlab", "acme"), "/tmp/x", CancellationToken.None));
    }
}
