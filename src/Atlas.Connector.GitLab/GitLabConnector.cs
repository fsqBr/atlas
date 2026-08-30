using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Atlas.Connector.Abstractions;
using Atlas.Connector.Git;
using Atlas.Domain.Sources;

namespace Atlas.Connector.GitLab;

public sealed class GitLabConnectorOptions
{
    public const string SectionName = "Atlas:Connectors:GitLab";

    /// <summary>Instance base URL (also the clone base); point at your self-managed instance when needed.</summary>
    public string BaseUrl { get; set; } = "https://gitlab.com";

    public int MaxProjects { get; set; } = 1000;
}

/// <summary>
/// GitLab (SaaS or self-managed) connector. Locators are full paths:
/// `group`, `group/subgroup` (discovery, subgroups included) or
/// `group/subgroup/project` (one project); gitlab.com URLs are accepted. A path
/// is first tried as a project, then as a group, then as a user. Discovery uses
/// the REST API v4 with the stored token; materialization delegates to the
/// generic git cloner (tokens work with any username through GIT_ASKPASS).
/// </summary>
public sealed class GitLabConnector(
    IHttpClientFactory httpClientFactory,
    IGitCloner git,
    GitLabConnectorOptions options,
    ICredentialProvider? credentials = null) : ISourceConnector
{
    public const string HttpClientName = "gitlab";

    public ConnectorDescriptor Descriptor { get; } = new(
        Id: "connector.gitlab",
        Name: "GitLab",
        Version: "0.1.0",
        Capabilities: ["discover", "materialize", "shallow-clone", "commit-fingerprint", "credentials"]);

    public bool CanHandle(SourceReference source) => source.Kind == SourceReference.Kinds.GitLab;

    public async Task<IReadOnlyList<RepositoryInfo>> DiscoverRepositoriesAsync(SourceReference source, CancellationToken cancellationToken)
    {
        var path = GitLabLocator.Parse(source.Locator);
        var token = await ResolveTokenAsync(source, cancellationToken);
        using var http = httpClientFactory.CreateClient(HttpClientName);
        var encoded = Uri.EscapeDataString(path);

        // 1. Exactly one project?
        try
        {
            using var project = await GetJsonAsync(http, $"projects/{encoded}", token, cancellationToken);
            return [ToInfo(project.RootElement)];
        }
        catch (GitLabApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }

        // 2. A group (with subgroups), else 3. a user namespace.
        var listPath = $"groups/{encoded}/projects?include_subgroups=true&archived=&simple=false&order_by=path&sort=asc&per_page=100";
        var result = new List<RepositoryInfo>();
        for (var page = 1; result.Count < options.MaxProjects; page++)
        {
            JsonDocument document;
            try
            {
                document = await GetJsonAsync(http, $"{listPath}&page={page}", token, cancellationToken);
            }
            catch (GitLabApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound && page == 1 && listPath.StartsWith("groups/", StringComparison.Ordinal) && !path.Contains('/'))
            {
                listPath = $"users/{encoded}/projects?per_page=100";
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
        var path = GitLabLocator.Parse(source.Locator);
        if (!path.Contains('/'))
        {
            throw new InvalidOperationException(
                $"GitLab locator '{source.Locator}' names a group or user, not a project. Discover projects first and assess one at a time.");
        }

        var cloneUrl = $"{options.BaseUrl.TrimEnd('/')}/{path}.git";
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
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{options.BaseUrl.TrimEnd('/')}/api/v4/{relativePath}"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Atlas", Descriptor.Version));
        if (token is not null)
        {
            request.Headers.Add("PRIVATE-TOKEN", token);
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var hint = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "the credential is missing or invalid",
                HttpStatusCode.Forbidden => "the credential lacks access",
                HttpStatusCode.NotFound => "not found (private projects need a credential)",
                _ => "unexpected response",
            };
            throw new GitLabApiException(response.StatusCode, $"GitLab API {(int)response.StatusCode} for {relativePath}: {hint}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    internal static RepositoryInfo ToInfo(JsonElement project) => new(
        Name: project.GetProperty("name").GetString()!,
        Locator: project.GetProperty("path_with_namespace").GetString()!,
        Kind: SourceReference.Kinds.GitLab,
        DefaultBranch: project.TryGetProperty("default_branch", out var branch) && branch.ValueKind == JsonValueKind.String ? branch.GetString() : null,
        Archived: project.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True,
        Language: null,
        LastPushUtc: project.TryGetProperty("last_activity_at", out var activity) && activity.ValueKind == JsonValueKind.String ? activity.GetDateTimeOffset() : null,
        IsPrivate: !(project.TryGetProperty("visibility", out var visibility) && visibility.GetString() == "public"));
}

public sealed class GitLabApiException(HttpStatusCode statusCode, string message) : InvalidOperationException(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

/// <summary>Namespace path (`group`, `group/subgroup/project`) or a GitLab URL; returns the normalized path.</summary>
public static class GitLabLocator
{
    public static string Parse(string locator)
    {
        var value = locator.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme is "https" or "http"))
        {
            value = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
            var marker = value.IndexOf("/-/", StringComparison.Ordinal);
            if (marker >= 0)
            {
                value = value[..marker];
            }
        }

        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(p => p.Contains("..", StringComparison.Ordinal) || !p.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.')))
        {
            throw new ArgumentException($"GitLab locator '{locator}' must be a namespace path such as 'group' or 'group/subgroup/project'.", nameof(locator));
        }

        return string.Join('/', parts);
    }
}
