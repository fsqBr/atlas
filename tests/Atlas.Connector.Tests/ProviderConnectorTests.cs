using System.Net;
using System.Text;
using Atlas.Connector.Abstractions;
using Atlas.Connector.AzureDevOps;
using Atlas.Connector.Git;
using Atlas.Connector.GitHub;
using Atlas.Domain.Sources;

namespace Atlas.Connector.Tests;

internal sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler, IHttpClientFactory
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

internal sealed class FakeCloner : IGitCloner
{
    public SourceReference? Last { get; private set; }

    public Task<MaterializedSource> CloneAsync(SourceReference gitSource, string targetDirectory, CancellationToken cancellationToken)
    {
        Last = gitSource;
        return Task.FromResult(new MaterializedSource(targetDirectory, IsBorrowed: false, CommitSha: "abc123"));
    }
}

internal sealed class FakeCredentials(string? username, string secret) : ICredentialProvider
{
    public Task<ConnectorCredentialValue?> ResolveAsync(Atlas.Domain.Sources.SourceReference source, CancellationToken cancellationToken) =>
        Task.FromResult<ConnectorCredentialValue?>(source.CredentialName == "gh" ? new ConnectorCredentialValue(username, secret) : null);
}

public class GitHubConnectorTests
{
    private static string Repo(string owner, string name, bool archived = false, string? language = "C#") =>
        $$"""{"name":"{{name}}","full_name":"{{owner}}/{{name}}","default_branch":"main","archived":{{(archived ? "true" : "false")}},"language":{{(language is null ? "null" : $"\"{language}\"")}},"pushed_at":"2026-08-01T10:00:00Z","private":false}""";

    [Theory]
    [InlineData("my-org", "my-org", null)]
    [InlineData("my-org/billing-api", "my-org", "billing-api")]
    [InlineData("https://github.com/my-org/billing-api.git", "my-org", "billing-api")]
    [InlineData(" https://github.com/my-org/billing-api/ ", "my-org", "billing-api")]
    public void Parses_locators(string locator, string owner, string? repo)
    {
        var parsed = GitHubLocator.Parse(locator);
        Assert.Equal((owner, repo), (parsed.Owner, parsed.Repository));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b/c")]
    [InlineData("../etc")]
    [InlineData("bad owner/repo")]
    public void Rejects_bad_locators(string locator) => Assert.Throws<ArgumentException>(() => GitHubLocator.Parse(locator));

    [Fact]
    public async Task Discovers_an_organization_with_pagination_and_sends_the_token()
    {
        var page1 = "[" + string.Join(",", Enumerable.Range(1, 100).Select(i => Repo("acme", $"repo-{i:000}"))) + "]";
        var page2 = "[" + Repo("acme", "zeta", archived: true) + "," + Repo("acme", "omega", language: null) + "]";
        var http = new FakeHttp(request => request.RequestUri!.Query.EndsWith("page=1", StringComparison.Ordinal) ? FakeHttp.Json(page1) : FakeHttp.Json(page2));
        var connector = new GitHubConnector(http, new FakeCloner(), new GitHubConnectorOptions(), new FakeCredentials(null, "ghp_x"));

        var repos = await connector.DiscoverRepositoriesAsync(new SourceReference("github", "acme", CredentialName: "gh"), CancellationToken.None);

        Assert.Equal(102, repos.Count);
        Assert.Equal(2, http.Requests.Count);
        Assert.All(http.Requests, r => Assert.Equal("Bearer ghp_x", r.Headers.Authorization!.ToString()));
        Assert.StartsWith("https://api.github.com/orgs/acme/repos?", http.Requests[0].RequestUri!.ToString());
        var zeta = Assert.Single(repos, r => r.Name == "zeta");
        Assert.True(zeta.Archived);
        Assert.Equal("acme/zeta", zeta.Locator);
        Assert.Equal("main", zeta.DefaultBranch);
        Assert.Null(Assert.Single(repos, r => r.Name == "omega").Language);
    }

    [Fact]
    public async Task Falls_back_to_user_repositories_when_the_owner_is_not_an_organization()
    {
        var http = new FakeHttp(request => request.RequestUri!.AbsolutePath.StartsWith("/orgs/")
            ? FakeHttp.Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound)
            : FakeHttp.Json("[" + Repo("jane", "dotfiles") + "]"));
        var connector = new GitHubConnector(http, new FakeCloner(), new GitHubConnectorOptions());

        var repos = await connector.DiscoverRepositoriesAsync(new SourceReference("github", "jane"), CancellationToken.None);

        Assert.Single(repos);
        Assert.Equal("/users/jane/repos", http.Requests[^1].RequestUri!.AbsolutePath);
        Assert.Null(http.Requests[0].Headers.Authorization);
    }

    [Fact]
    public async Task Single_repository_locator_discovers_exactly_that_repository()
    {
        var http = new FakeHttp(_ => FakeHttp.Json(Repo("acme", "billing-api")));
        var connector = new GitHubConnector(http, new FakeCloner(), new GitHubConnectorOptions());

        var repos = await connector.DiscoverRepositoriesAsync(new SourceReference("github", "acme/billing-api"), CancellationToken.None);

        Assert.Equal("/repos/acme/billing-api", Assert.Single(http.Requests).RequestUri!.AbsolutePath);
        Assert.Equal("acme/billing-api", Assert.Single(repos).Locator);
    }

    [Fact]
    public async Task Unauthorized_is_explained()
    {
        var http = new FakeHttp(_ => FakeHttp.Json("""{"message":"Bad credentials"}""", HttpStatusCode.Unauthorized));
        var connector = new GitHubConnector(http, new FakeCloner(), new GitHubConnectorOptions());
        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => connector.DiscoverRepositoriesAsync(new SourceReference("github", "acme/x"), CancellationToken.None));
        Assert.Contains("credential", ex.Message);
    }

    [Fact]
    public async Task Materializes_through_the_git_cloner_with_branch_and_credential()
    {
        var cloner = new FakeCloner();
        var connector = new GitHubConnector(new FakeHttp(_ => throw new InvalidOperationException("no http expected")), cloner, new GitHubConnectorOptions());

        var result = await connector.MaterializeAsync(new SourceReference("github", "acme/billing-api", "release/1.0", "gh"), "/tmp/x", CancellationToken.None);

        Assert.Equal("abc123", result.CommitSha);
        Assert.Equal(new SourceReference("git", "https://github.com/acme/billing-api.git", "release/1.0", "gh"), cloner.Last);
    }

    [Fact]
    public async Task Owner_only_locator_cannot_be_materialized()
    {
        var connector = new GitHubConnector(new FakeHttp(_ => throw new InvalidOperationException()), new FakeCloner(), new GitHubConnectorOptions());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => connector.MaterializeAsync(new SourceReference("github", "acme"), "/tmp/x", CancellationToken.None));
        Assert.Contains("Discover", ex.Message);
    }
}

public class AzureDevOpsConnectorTests
{
    private const string Repositories = """
        {"value":[
          {"id":"1","name":"billing-api","defaultBranch":"refs/heads/main","isDisabled":false,"project":{"name":"Payments","visibility":"private"}},
          {"id":"2","name":"Legacy Portal","defaultBranch":"refs/heads/develop","isDisabled":true,"project":{"name":"Payments","visibility":"private"}},
          {"id":"3","name":"docs","project":{"name":"Payments","visibility":"public"}}
        ],"count":3}
        """;

    [Theory]
    [InlineData("contoso/Payments", "contoso", "Payments", null)]
    [InlineData("contoso/Payments/billing-api", "contoso", "Payments", "billing-api")]
    [InlineData("https://dev.azure.com/contoso/Payments/_git/billing-api", "contoso", "Payments", "billing-api")]
    [InlineData("https://dev.azure.com/contoso/My%20Project/_git/Legacy%20Portal", "contoso", "My Project", "Legacy Portal")]
    public void Parses_locators(string locator, string org, string project, string? repo)
    {
        var parsed = AzureDevOpsLocator.Parse(locator);
        Assert.Equal((org, project, repo), (parsed.Organization, parsed.Project, parsed.Repository));
    }

    [Theory]
    [InlineData("contoso")]
    [InlineData("a/b/c/d")]
    public void Rejects_bad_locators(string locator) => Assert.Throws<ArgumentException>(() => AzureDevOpsLocator.Parse(locator));

    [Fact]
    public async Task Discovers_project_repositories_with_basic_pat_auth()
    {
        var http = new FakeHttp(_ => FakeHttp.Json(Repositories));
        var connector = new AzureDevOpsConnector(http, new FakeCloner(), new AzureDevOpsConnectorOptions(), new FakeCredentials(null, "pat123"));

        var repos = await connector.DiscoverRepositoriesAsync(new SourceReference("azure-devops", "contoso/Payments", CredentialName: "gh"), CancellationToken.None);

        var request = Assert.Single(http.Requests);
        Assert.Equal("https://dev.azure.com/contoso/Payments/_apis/git/repositories?api-version=7.1", request.RequestUri!.ToString());
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":pat123")), request.Headers.Authorization!.ToString());

        Assert.Equal(["billing-api", "docs", "Legacy Portal"], repos.Select(r => r.Name).ToArray());
        var legacy = repos.Single(r => r.Name == "Legacy Portal");
        Assert.True(legacy.Archived);
        Assert.Equal("develop", legacy.DefaultBranch);
        Assert.Equal("contoso/Payments/Legacy Portal", legacy.Locator);
        Assert.False(repos.Single(r => r.Name == "docs").IsPrivate);
        Assert.Null(repos.Single(r => r.Name == "docs").DefaultBranch);
    }

    [Fact]
    public async Task Repository_locator_filters_discovery_to_that_repository()
    {
        var connector = new AzureDevOpsConnector(new FakeHttp(_ => FakeHttp.Json(Repositories)), new FakeCloner(), new AzureDevOpsConnectorOptions());
        var repos = await connector.DiscoverRepositoriesAsync(new SourceReference("azure-devops", "contoso/Payments/billing-api"), CancellationToken.None);
        Assert.Equal("billing-api", Assert.Single(repos).Name);
    }

    [Fact]
    public async Task Sign_in_redirect_203_is_reported_as_a_credential_problem()
    {
        var connector = new AzureDevOpsConnector(new FakeHttp(_ => new HttpResponseMessage((HttpStatusCode)203) { Content = new StringContent("<html>sign in</html>") }), new FakeCloner(), new AzureDevOpsConnectorOptions());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => connector.DiscoverRepositoriesAsync(new SourceReference("azure-devops", "contoso/Payments"), CancellationToken.None));
        Assert.Contains("credential", ex.Message);
    }

    [Fact]
    public async Task Materializes_with_an_escaped_git_url()
    {
        var cloner = new FakeCloner();
        var connector = new AzureDevOpsConnector(new FakeHttp(_ => throw new InvalidOperationException()), cloner, new AzureDevOpsConnectorOptions());

        await connector.MaterializeAsync(new SourceReference("azure-devops", "contoso/My Project/Legacy Portal", null, "ado"), "/tmp/x", CancellationToken.None);

        Assert.Equal("https://dev.azure.com/contoso/My%20Project/_git/Legacy%20Portal", cloner.Last!.Locator);
        Assert.Equal("git", cloner.Last.Kind);
        Assert.Equal("ado", cloner.Last.CredentialName);
    }

    [Fact]
    public async Task Project_only_locator_cannot_be_materialized()
    {
        var connector = new AzureDevOpsConnector(new FakeHttp(_ => throw new InvalidOperationException()), new FakeCloner(), new AzureDevOpsConnectorOptions());
        await Assert.ThrowsAsync<InvalidOperationException>(() => connector.MaterializeAsync(new SourceReference("azure-devops", "contoso/Payments"), "/tmp/x", CancellationToken.None));
    }
}
