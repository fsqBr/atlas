using System.Text.Json;
using Atlas.Scanner.Ai.Catalog;
using Atlas.Security.Redaction;

namespace Atlas.Scanner.Ai.Mcp;

/// <summary>A literal credential found in an MCP server definition: which key, and only its fingerprint + preview.</summary>
public sealed record McpSecretHit(string Location, string Fingerprint, string Preview);

public sealed record McpServerFact(
    string ConfigPath,
    string Name,
    /// <summary>stdio | http | sse | unknown.</summary>
    string Transport,
    /// <summary>Executable name only (no arguments) for stdio servers.</summary>
    string? Command,
    /// <summary>Host (and port) only for remote servers — never the full URL, which may carry tokens in the query.</summary>
    string? Host,
    bool IsRemote,
    string? KnownServerName,
    /// <summary>Read | Write | ReadWrite | Execute | None | Unknown.</summary>
    string Capability,
    IReadOnlyList<string> EnvKeys,
    IReadOnlyList<McpSecretHit> Secrets);

/// <summary>
/// Reads the MCP client configuration files that live in repositories (Claude Code, Cursor, VS Code,
/// Windsurf, Gemini CLI, Continue…). Tolerant of comments and trailing commas; a file that is not an
/// object or has no server section yields nothing. Values that look like credentials are fingerprinted
/// at the source — the value never leaves this method.
/// </summary>
public static class McpConfigReader
{
    private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    private static readonly string[] ServerSections = ["mcpServers", "servers", "mcp_servers"];

    public static IReadOnlyList<McpServerFact> Read(string configPath, string json, SignatureCatalog catalog, byte[]? hmacKey)
    {
        var servers = new List<McpServerFact>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, Options);
        }
        catch (JsonException)
        {
            return servers;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return servers;
            }

            foreach (var (name, element) in EnumerateServers(root))
            {
                servers.Add(Describe(configPath, name, element, catalog, hmacKey));
            }
        }

        return servers.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<(string Name, JsonElement Element)> EnumerateServers(JsonElement root)
    {
        foreach (var section in ServerSections)
        {
            if (root.TryGetProperty(section, out var servers))
            {
                if (servers.ValueKind == JsonValueKind.Object)
                {
                    foreach (var server in servers.EnumerateObject())
                    {
                        yield return (server.Name, server.Value);
                    }
                }
                else if (servers.ValueKind == JsonValueKind.Array)
                {
                    var index = 0;
                    foreach (var server in servers.EnumerateArray())
                    {
                        yield return (server.ValueKind == JsonValueKind.Object && server.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : $"server-{index}", server);
                        index++;
                    }
                }
            }
        }

        // Continue: { "experimental": { "modelContextProtocolServers": [ { "transport": { "type": "stdio", "command": ... } } ] } }
        if (root.TryGetProperty("experimental", out var experimental) && experimental.ValueKind == JsonValueKind.Object
            && experimental.TryGetProperty("modelContextProtocolServers", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var server in list.EnumerateArray())
            {
                var inner = server.ValueKind == JsonValueKind.Object && server.TryGetProperty("transport", out var t) && t.ValueKind == JsonValueKind.Object ? t : server;
                yield return ($"continue-{index}", inner);
                index++;
            }
        }
    }

    private static McpServerFact Describe(string configPath, string name, JsonElement server, SignatureCatalog catalog, byte[]? hmacKey)
    {
        string? command = null, host = null, url = null, type = null;
        var args = new List<string>();
        var envKeys = new List<string>();
        var secrets = new List<McpSecretHit>();

        if (server.ValueKind == JsonValueKind.Object)
        {
            if (server.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String)
            {
                command = Path.GetFileName(c.GetString()!.Trim());
            }

            if (server.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
            {
                type = t.GetString()!.Trim().ToLowerInvariant();
            }

            if (server.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
            {
                url = u.GetString()!.Trim();
                host = HostOf(url);
            }

            if (server.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array)
            {
                foreach (var arg in a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!))
                {
                    args.Add(arg);
                    var candidate = arg.Contains('=') ? arg[(arg.IndexOf('=') + 1)..] : arg;
                    if (SecretValues.LooksLikeSecret(candidate) && !candidate.Contains('/') && !candidate.Contains('\\'))
                    {
                        secrets.Add(new McpSecretHit("args", HmacFingerprint.Compute(hmacKey, candidate), SecretValues.Preview(candidate)));
                    }
                }
            }

            if (server.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
            {
                foreach (var variable in env.EnumerateObject())
                {
                    envKeys.Add(variable.Name);
                    if (variable.Value.ValueKind == JsonValueKind.String && SecretValues.LooksLikeSecret(variable.Value.GetString()))
                    {
                        var value = variable.Value.GetString()!;
                        secrets.Add(new McpSecretHit($"env.{variable.Name}", HmacFingerprint.Compute(hmacKey, value), SecretValues.Preview(value)));
                    }
                }
            }

            if (server.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
            {
                foreach (var header in headers.EnumerateObject())
                {
                    if (header.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = header.Value.GetString()!;
                        var token = value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..] : value;
                        if (SecretValues.LooksLikeSecret(token))
                        {
                            secrets.Add(new McpSecretHit($"headers.{header.Name}", HmacFingerprint.Compute(hmacKey, token), SecretValues.Preview(token)));
                        }
                    }
                }
            }
        }

        var transport = type switch
        {
            "stdio" => "stdio",
            "http" or "streamable-http" or "streamablehttp" => "http",
            "sse" => "sse",
            _ => url is not null ? "http" : command is not null ? "stdio" : "unknown",
        };

        var isRemote = host is not null && !IsLocalHost(host);
        var haystack = string.Join(' ', new[] { name, command ?? string.Empty, url ?? string.Empty }.Concat(args)).ToLowerInvariant();
        var known = catalog.Document.Mcp.KnownServers
            .Where(k => haystack.Contains(k.Match, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(k => k.Match.Length)
            .FirstOrDefault();

        return new McpServerFact(
            configPath, name, transport, command, host, isRemote,
            known?.Name, known?.Capability ?? "Unknown",
            envKeys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
            secrets);
    }

    private static string? HostOf(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        }

        return null;
    }

    private static bool IsLocalHost(string host)
    {
        var name = host.Split(':')[0];
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("127.", StringComparison.Ordinal)
            || name == "0.0.0.0"
            || name.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase)
            || name == "::1" || name == "[::1]";
    }
}
