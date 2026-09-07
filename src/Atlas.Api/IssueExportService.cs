using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Application.Assessments;
using Atlas.Application.Credentials;
using Atlas.Application.Findings;
using Atlas.Application.Tenants;
using Atlas.Domain.Assessments;
using Atlas.Domain.Findings;
using Atlas.Domain.Rules;
using Atlas.Domain.Sources;

namespace Atlas.Api;

public sealed record IssueExportResult(int Created, IReadOnlyList<string> Urls, IReadOnlyList<string> Errors);

/// <summary>
/// Turns the top open findings into issues/work items on the assessed repository's own tracker
/// (GitHub issues, Azure DevOps work items), so the assessment becomes an executable backlog.
/// Uses the assessment's stored credential — the same one that clones the repository.
/// </summary>
public sealed class IssueExportService(
    IHttpClientFactory httpClientFactory,
    ICredentialRepository credentials,
    ISecretCipher cipher,
    ITenantContext tenant,
    ILogger<IssueExportService> logger)
{
    public const int MaxIssues = 50;

    public async Task<IssueExportResult> ExportAsync(
        Assessment assessment,
        IReadOnlyList<FindingWithLatestOccurrence> findings,
        IReadOnlyDictionary<string, RuleDefinition> rules,
        string publicBaseUrl,
        string? lang,
        CancellationToken cancellationToken)
    {
        var secret = await ResolveSecretAsync(assessment, cancellationToken);
        var urls = new List<string>();
        var errors = new List<string>();

        foreach (var item in findings)
        {
            var (title, body) = Compose(assessment, item, rules, publicBaseUrl, lang);
            try
            {
                var url = assessment.SourceKind switch
                {
                    SourceReference.Kinds.GitHub => await CreateGitHubIssueAsync(assessment.SourceLocator, secret, title, body, cancellationToken),
                    SourceReference.Kinds.AzureDevOps => await CreateAdoWorkItemAsync(assessment.SourceLocator, secret, title, body, cancellationToken),
                    _ => throw new InvalidOperationException($"Issue export supports github and azure-devops sources; this assessment is '{assessment.SourceKind}'."),
                };
                urls.Add(url);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Issue export failed for finding {FindingId}.", item.Finding.Id);
                errors.Add($"{title}: {ex.Message}");
                if (errors.Count >= 3)
                {
                    break; // three consecutive failures: wrong permissions, stop hammering the API
                }
            }
        }

        return new IssueExportResult(urls.Count, urls, errors);
    }

    private async Task<string> ResolveSecretAsync(Assessment assessment, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(assessment.CredentialName))
        {
            throw new InvalidOperationException("The assessment has no stored credential; issue export needs one with issue/work-item write scope.");
        }

        var credential = await credentials.GetByNameAsync(tenant.Require(), assessment.CredentialName, cancellationToken)
            ?? throw new InvalidOperationException($"Credential '{assessment.CredentialName}' not found.");
        var secret = Encoding.UTF8.GetString(cipher.Unprotect(credential.Envelope));
        credential.MarkUsed();
        return secret;
    }

    private static (string Title, string Body) Compose(
        Assessment assessment,
        FindingWithLatestOccurrence item,
        IReadOnlyDictionary<string, RuleDefinition> rules,
        string publicBaseUrl,
        string? lang)
    {
        var text = FindingLocalizer.Localize(item.Finding, item.Latest, rules.GetValueOrDefault(item.Finding.RuleId), lang);
        // Repository-controlled strings go into tracker markdown: strip what would break out of the
        // code span or forge links (backticks, brackets, newlines in paths).
        var location = item.Latest?.Evidence.FilePath is { } file
            ? $"`{Sanitize(file)}`{(item.Latest.Evidence.LineStart is { } line ? $" (line {line})" : "")}"
            : "_estate-level_";
        var link = string.IsNullOrWhiteSpace(publicBaseUrl)
            ? ""
            : $"\n\n[Open in Atlas]({publicBaseUrl.TrimEnd('/')}/assessments/{assessment.Id}?tab=findings&finding={item.Finding.Id})";
        var body =
            $"**Severity:** {item.Finding.Severity} · **Rule:** `{item.Finding.RuleId}` · **Category:** {item.Finding.Category}\n\n" +
            $"{Sanitize(text.Message)}\n\n**Where:** {location}\n" +
            (string.IsNullOrWhiteSpace(text.Remediation) ? "" : $"\n**Remediation:** {Sanitize(text.Remediation)}\n") +
            link +
            "\n\n---\n_Exported from an Atlas assessment; counts and text reflect the run at export time._";
        return ($"[Atlas][{item.Finding.Severity}] {Truncate(text.Title, 180)}", Truncate(body, 60_000));
    }

    private async Task<string> CreateGitHubIssueAsync(string locator, string secret, string title, string body, CancellationToken cancellationToken)
    {
        // Locator: https://github.com/{owner}/{repo}(.git)
        if (!Uri.TryCreate(locator, UriKind.Absolute, out var locatorUri))
        {
            throw new InvalidOperationException($"The locator '{locator}' is not an absolute URL; SCP-style git addresses are not supported for issue export.");
        }

        var parts = locatorUri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2)
        {
            throw new InvalidOperationException($"Cannot read owner/repo from '{locator}'.");
        }

        var repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        using var http = httpClientFactory.CreateClient("issue-export");
        http.Timeout = TimeSpan.FromSeconds(30);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.github.com/repos/{parts[0]}/{repo}/issues")
        {
            Content = new StringContent(new JsonObject { ["title"] = title, ["body"] = body, ["labels"] = new JsonArray("atlas") }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + secret);
        request.Headers.TryAddWithoutValidation("User-Agent", "atlas-assessment");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

        using var response = await http.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub answered {(int)response.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.GetProperty("html_url").GetString() ?? "";
    }

    private async Task<string> CreateAdoWorkItemAsync(string locator, string secret, string title, string body, CancellationToken cancellationToken)
    {
        // Locator: https://dev.azure.com/{org}/{project}/_git/{repo}
        if (!Uri.TryCreate(locator, UriKind.Absolute, out var locatorUri))
        {
            throw new InvalidOperationException($"The locator '{locator}' is not an absolute URL; SCP-style git addresses are not supported for issue export.");
        }

        var parts = locatorUri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2)
        {
            throw new InvalidOperationException($"Cannot read organization/project from '{locator}'.");
        }

        var org = parts[0];
        var project = Uri.UnescapeDataString(parts[1]);
        using var http = httpClientFactory.CreateClient("issue-export");
        http.Timeout = TimeSpan.FromSeconds(30);
        var patch = new JsonArray(
            new JsonObject { ["op"] = "add", ["path"] = "/fields/System.Title", ["value"] = title },
            new JsonObject { ["op"] = "add", ["path"] = "/fields/System.Description", ["value"] = MiniHtml(body) },
            new JsonObject { ["op"] = "add", ["path"] = "/fields/System.Tags", ["value"] = "atlas" });
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://dev.azure.com/{org}/{Uri.EscapeDataString(project)}/_apis/wit/workitems/$Issue?api-version=7.1")
        {
            Content = new StringContent(patch.ToJsonString(), Encoding.UTF8, "application/json-patch+json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + secret)));

        using var response = await http.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Azure DevOps answered {(int)response.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.GetProperty("_links").GetProperty("html").GetProperty("href").GetString() ?? "";
    }

    private static string Sanitize(string value) =>
        value.Replace("`", "'").Replace("[", "(").Replace("]", ")").Replace("\r", " ").Replace("\n", " ");

    private static string MiniHtml(string markdown) =>
        "<div>" + System.Net.WebUtility.HtmlEncode(markdown).Replace("\n", "<br/>") + "</div>";

    private static string Truncate(string value, int max)
    {
        if (value.Length <= max)
        {
            return value;
        }

        // Never split a surrogate pair: that would produce an invalid JSON string.
        var end = char.IsHighSurrogate(value[max - 1]) ? max - 1 : max;
        return value[..end];
    }
}
