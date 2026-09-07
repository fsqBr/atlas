namespace Atlas.Connector.Git;

public sealed class GitConnectorOptions
{
    public const string SectionName = "Atlas:Connectors:Git";

    /// <summary>
    /// Hosts a clone may reach (exact host, or `*.example.com` for subdomains). Empty = any host.
    /// Defense in depth for the worker's egress: a locator pointing anywhere else is refused before git runs.
    /// </summary>
    public string[] AllowedHosts { get; set; } = [];

    /// <summary>Allow `file://` URLs and plain paths (bind-mounted sources). Default true; turn off on shared servers.</summary>
    public bool AllowFileUrls { get; set; } = true;

    /// <summary>
    /// Months of commit history to fetch and read (churn, authorship). 0 = shallow clone, no history.
    /// With a value, clones use --shallow-since instead of --depth 1.
    /// </summary>
    public int HistoryMonths { get; set; }
}

public sealed class GitHostNotAllowedException(string locator, string reason)
    : InvalidOperationException($"Git source '{locator}' refused: {reason}")
{
    public string Locator { get; } = locator;
}

/// <summary>Decides whether a git locator may be cloned under the configured egress policy.</summary>
public static class GitHostPolicy
{
    public static void EnsureAllowed(string locator, GitConnectorOptions options)
    {
        var value = locator.Trim();

        // scp-like syntax (git@host:path) is a host reference too.
        if (!value.Contains("://", StringComparison.Ordinal) && value.Contains(':') && !Path.IsPathRooted(value))
        {
            var host = value[(value.IndexOf('@') + 1)..value.IndexOf(':')];
            EnsureHostAllowed(locator, host, options);
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme == "file" || uri.IsFile)
        {
            if (!options.AllowFileUrls)
            {
                throw new GitHostNotAllowedException(locator, "local paths and file:// URLs are disabled (Atlas:Connectors:Git:AllowFileUrls).");
            }

            return;
        }

        if (uri.Scheme is not ("https" or "http" or "ssh" or "git"))
        {
            throw new GitHostNotAllowedException(locator, $"scheme '{uri.Scheme}' is not supported.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) && uri.UserInfo.Contains(':'))
        {
            throw new GitHostNotAllowedException(locator, "credentials embedded in the URL are not accepted; store a credential and reference it by name.");
        }

        EnsureHostAllowed(locator, uri.Host, options);
    }

    private static void EnsureHostAllowed(string locator, string host, GitConnectorOptions options)
    {
        if (options.AllowedHosts.Length == 0)
        {
            return;
        }

        foreach (var pattern in options.AllowedHosts)
        {
            var p = pattern.Trim();
            if (p.Length == 0)
            {
                continue;
            }

            if (p.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = p[1..];
                if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || host.Equals(p[2..], StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            else if (host.Equals(p, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw new GitHostNotAllowedException(locator, $"host '{host}' is not in Atlas:Connectors:Git:AllowedHosts ({string.Join(", ", options.AllowedHosts)}).");
    }
}
