using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Atlas.Connector.Abstractions;
using Atlas.Connector.Git;
using Atlas.Domain.Sources;

namespace Atlas.Connector.AzureDevOps;

public sealed class AzureDevOpsConnectorOptions
{
    public const string SectionName = "Atlas:Connectors:AzureDevOps";

    /// <summary>Base URL; point at https://tfs.example.com/tfs/DefaultCollection for on-premises Azure DevOps Server.</summary>
    public string BaseUrl { get; set; } = "https://dev.azure.com";

    public string ApiVersion { get; set; } = "7.1";
}

/// <summary>
/// Azure DevOps (Services or Server) connector. Locators are `org/project`
/// (discovery) or `org/project/repo` (one repository); dev.azure.com `_git`
/// URLs are accepted too. Discovery uses the Git REST API with the stored PAT as
/// basic auth; materialization delegates to the generic git cloner (PATs work
/// with any username through GIT_ASKPASS). Nothing is executed from the repository.
/// </summary>
public sealed class AzureDevOpsConnector(
    IHttpClientFactory httpClientFactory,
    IGitCloner git,
    AzureDevOpsConnectorOptions options,
    ICredentialProvider? credentials = null) : ISourceConnector
{
    public const string HttpClientName = "azure-devops";

    public ConnectorDescriptor Descriptor { get; } = new(
        Id: "connector.azure-devops",
        Name: "Azure DevOps",
        Version: "0.1.0",
        Capabilities: ["discover", "materialize", "shallow-clone", "commit-fingerprint", "credentials"]);

    public bool CanHandle(SourceReference source) => source.Kind == SourceReference.Kinds.AzureDevOps;

    public async Task<IReadOnlyList<RepositoryInfo>> DiscoverRepositoriesAsync(SourceReference source, CancellationToken cancellationToken)
    {
        var locator = AzureDevOpsLocator.Parse(source.Locator);
        var token = await ResolveTokenAsync(source, cancellationToken);
        using var http = httpClientFactory.CreateClient(HttpClientName);

        var url = $"{options.BaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(locator.Organization)}/{Uri.EscapeDataString(locator.Project)}/_apis/git/repositories?api-version={options.ApiVersion}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (token is not null)
        {
            // PATs are sent as basic auth with an empty user name.
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + token)));
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NonAuthoritativeInformation || (int)response.StatusCode == 203)
        {
            // Azure DevOps answers 203 with a sign-in page when the PAT is missing or invalid.
            throw new InvalidOperationException("Azure DevOps rejected the request (sign-in required): the credential is missing or invalid.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var hint = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "the credential is missing or invalid",
                HttpStatusCode.Forbidden => "the credential lacks access to this project",
                HttpStatusCode.NotFound => "organization or project not found",
                _ => "unexpected response",
            };
            throw new InvalidOperationException($"Azure DevOps API {(int)response.StatusCode} for {locator.Organization}/{locator.Project}: {hint}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var result = new List<RepositoryInfo>();
        foreach (var repo in document.RootElement.GetProperty("value").EnumerateArray())
        {
            var info = ToInfo(repo, locator);
            if (locator.Repository is null || string.Equals(info.Name, locator.Repository, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(info);
            }
        }

        return result.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Task<MaterializedSource> MaterializeAsync(SourceReference source, string targetDirectory, CancellationToken cancellationToken)
    {
        var locator = AzureDevOpsLocator.Parse(source.Locator);
        if (locator.Repository is null)
        {
            throw new InvalidOperationException(
                $"Azure DevOps locator '{source.Locator}' names a project, not a repository. Discover repositories first and assess one at a time.");
        }

        var cloneUrl = $"{options.BaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(locator.Organization)}/{Uri.EscapeDataString(locator.Project)}/_git/{Uri.EscapeDataString(locator.Repository)}";
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

    internal static RepositoryInfo ToInfo(JsonElement repo, AzureDevOpsLocator locator)
    {
        var name = repo.GetProperty("name").GetString()!;
        var defaultBranch = repo.TryGetProperty("defaultBranch", out var branch) && branch.ValueKind == JsonValueKind.String
            ? branch.GetString()!.Replace("refs/heads/", string.Empty, StringComparison.Ordinal)
            : null;
        return new RepositoryInfo(
            Name: name,
            Locator: $"{locator.Organization}/{locator.Project}/{name}",
            Kind: SourceReference.Kinds.AzureDevOps,
            DefaultBranch: defaultBranch,
            Archived: repo.TryGetProperty("isDisabled", out var disabled) && disabled.ValueKind == JsonValueKind.True,
            Language: null,
            LastPushUtc: null,
            IsPrivate: !(repo.TryGetProperty("project", out var project) && project.TryGetProperty("visibility", out var visibility) && visibility.GetString() == "public"));
    }
}

/// <summary>`org/project`, `org/project/repo`, or a dev.azure.com URL (`https://dev.azure.com/org/project/_git/repo`).</summary>
public sealed record AzureDevOpsLocator(string Organization, string Project, string? Repository)
{
    public static AzureDevOpsLocator Parse(string locator)
    {
        var value = locator.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme is "https" or "http"))
        {
            value = Uri.UnescapeDataString(uri.AbsolutePath).Replace("/_git/", "/", StringComparison.OrdinalIgnoreCase).Trim('/');
        }

        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 3 || parts.Any(p => p.Contains("..", StringComparison.Ordinal) || p.Contains('\\')))
        {
            throw new ArgumentException($"Azure DevOps locator '{locator}' must be 'org/project' or 'org/project/repo'.", nameof(locator));
        }

        return new AzureDevOpsLocator(parts[0], parts[1], parts.Length == 3 ? parts[2] : null);
    }
}
