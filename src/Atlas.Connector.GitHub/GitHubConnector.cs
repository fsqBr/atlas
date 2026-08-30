using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Atlas.Connector.Abstractions;
using Atlas.Connector.Git;
using Atlas.Domain.Sources;

namespace Atlas.Connector.GitHub;

public sealed class GitHubConnectorOptions
{
    public const string SectionName = "Atlas:Connectors:GitHub";

    /// <summary>REST base URL; point at https://ghes.example.com/api/v3 for GitHub Enterprise Server.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.github.com";

    /// <summary>Clone base URL.</summary>
    public string WebBaseUrl { get; set; } = "https://github.com";

    public int MaxRepositories { get; set; } = 1000;
}

/// <summary>
/// GitHub provider connector. Locators are `owner` (organization
/// or user — discovery) or `owner/repo` (one repository — materialization); full
/// github.com URLs are accepted too. Discovery uses the REST API with the stored
/// credential as a bearer token; materialization delegates to the generic git
/// cloner (same credential, GIT_ASKPASS). Nothing is executed from the repository.
/// </summary>
public sealed class GitHubConnector(
    IHttpClientFactory httpClientFactory,
    IGitCloner git,
    GitHubConnectorOptions options,
    ICredentialProvider? credentials = null) : ISourceConnector
{
    public const string HttpClientName = "github";

    public ConnectorDescriptor Descriptor { get; } = new(
        Id: "connector.github",
        Name: "GitHub",
        Version: "0.1.0",
        Capabilities: ["discover", "materialize", "shallow-clone", "commit-fingerprint", "credentials"]);

    public bool CanHandle(SourceReference source) => source.Kind == SourceReference.Kinds.GitHub;

    public async Task<IReadOnlyList<RepositoryInfo>> DiscoverRepositoriesAsync(SourceReference source, CancellationToken cancellationToken)
    {
        var locator = GitHubLocator.Parse(source.Locator);
        var token = await ResolveTokenAsync(source, cancellationToken);
        using var http = httpClientFactory.CreateClient(HttpClientName);

        if (locator.Repository is not null)
        {
            using var document = await GetJsonAsync(http, $"repos/{locator.Owner}/{locator.Repository}", token, cancellationToken);
            return [ToInfo(document.RootElement)];
        }

        var result = new List<RepositoryInfo>();
        var path = $"orgs/{locator.Owner}/repos";
        for (var page = 1; result.Count < options.MaxRepositories; page++)
        {
            JsonDocument document;
            try
            {
                document = await GetJsonAsync(http, $"{path}?per_page=100&type=all&sort=full_name&page={page}", token, cancellationToken);
            }
            catch (GitHubApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound && page == 1 && path.StartsWith("orgs/", StringComparison.Ordinal))
            {
                // Not an organization: list the user's repositories instead.
                path = $"users/{locator.Owner}/repos";
                page = 0;
                continue;
            }

            using (document)
            {
                var count = 0;
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    result.Add(ToInfo(item));
                    count++;
                }

                if (count < 100)
                {
                    break;
                }
            }
        }

        return result;
    }

    public Task<MaterializedSource> MaterializeAsync(SourceReference source, string targetDirectory, CancellationToken cancellationToken)
    {
        var locator = GitHubLocator.Parse(source.Locator);
        if (locator.Repository is null)
        {
            throw new InvalidOperationException(
                $"GitHub locator '{source.Locator}' names an owner, not a repository. Discover repositories first and assess one at a time.");
        }

        var cloneUrl = $"{options.WebBaseUrl.TrimEnd('/')}/{locator.Owner}/{locator.Repository}.git";
        return git.CloneAsync(new SourceReference(SourceReference.Kinds.Git, cloneUrl, source.Branch, source.CredentialName), targetDirectory, cancellationToken);
    }

    private async Task<string?> ResolveTokenAsync(SourceReference source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.CredentialName))
        {
            return null;
        }

        if (credentials is null)
        {
            throw new InvalidOperationException($"Source requires credential '{source.CredentialName}' but no credential provider is configured.");
        }

        var value = await credentials.ResolveAsync(source, cancellationToken)
            ?? throw new InvalidOperationException($"Credential '{source.CredentialName}' was not found.");
        return value.Secret;
    }

    private async Task<JsonDocument> GetJsonAsync(HttpClient http, string relativePath, string? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.ApiBaseUrl.TrimEnd('/') + "/" + relativePath));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Atlas", Descriptor.Version));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var hint = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "the credential is missing or invalid",
                HttpStatusCode.Forbidden => "the credential lacks access (or the API rate limit was hit — use a credential)",
                HttpStatusCode.NotFound => "owner or repository not found (private repositories need a credential)",
                _ => "unexpected response",
            };
            throw new GitHubApiException(response.StatusCode, $"GitHub API {(int)response.StatusCode} for {relativePath}: {hint}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    internal static RepositoryInfo ToInfo(JsonElement repo)
    {
        var fullName = repo.GetProperty("full_name").GetString()!;
        return new RepositoryInfo(
            Name: repo.GetProperty("name").GetString()!,
            Locator: fullName,
            Kind: SourceReference.Kinds.GitHub,
            DefaultBranch: repo.TryGetProperty("default_branch", out var branch) ? branch.GetString() : null,
            Archived: repo.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True,
            Language: repo.TryGetProperty("language", out var language) && language.ValueKind == JsonValueKind.String ? language.GetString() : null,
            LastPushUtc: repo.TryGetProperty("pushed_at", out var pushed) && pushed.ValueKind == JsonValueKind.String ? pushed.GetDateTimeOffset() : null,
            IsPrivate: repo.TryGetProperty("private", out var isPrivate) && isPrivate.ValueKind == JsonValueKind.True);
    }
}

public sealed class GitHubApiException(HttpStatusCode statusCode, string message) : InvalidOperationException(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

/// <summary>`owner`, `owner/repo`, or a github.com URL (`https://github.com/owner/repo(.git)`).</summary>
public sealed record GitHubLocator(string Owner, string? Repository)
{
    public static GitHubLocator Parse(string locator)
    {
        var value = locator.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme is "https" or "http"))
        {
            value = uri.AbsolutePath.Trim('/');
        }

        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is 0 or > 2 || parts.Any(p => p.Contains("..", StringComparison.Ordinal) || !p.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.')))
        {
            throw new ArgumentException($"GitHub locator '{locator}' must be 'owner' or 'owner/repo'.", nameof(locator));
        }

        var repo = parts.Length == 2 ? parts[1] : null;
        if (repo is not null && repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repo = repo[..^4];
        }

        return new GitHubLocator(parts[0], repo);
    }
}
