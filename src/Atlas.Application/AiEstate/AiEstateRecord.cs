using System.Text.Json;

namespace Atlas.Application.AiEstate;

/// <summary>
/// Rule ids the AI Estate scanner emits (Atlas.Scanner.Ai). Kept as strings here on purpose: the
/// application and reporting layers read persisted findings and must not depend on a scanner assembly.
/// </summary>
public static class AiEstateRuleIds
{
    public const string ScannerId = "ai.estate";
    public const string Inventory = "ai.inventory";
    public const string ProviderSdk = "ai.provider.sdk";
    public const string DirectEndpoint = "ai.provider.direct-endpoint";
    public const string RetiredModel = "ai.model.retired";
    public const string AgentFramework = "ai.agent.framework";
    public const string McpServer = "ai.mcp.server";
    public const string McpRemoteServer = "ai.mcp.remote-server";
    public const string McpSecretInConfig = "ai.mcp.secret-in-config";
    public const string LocalRuntime = "ai.local-runtime";
    public const string UnapprovedProvider = "ai.unapproved-provider";

    public const string Prefix = "ai.";

    /// <summary>Privacy scanner rules that identify personal data in code — the PII side of the AI × PII correlation.</summary>
    public const string PiiPrefix = "privacy.pii.";

    public static bool IsAiRule(string ruleId) => ruleId.StartsWith(Prefix, StringComparison.Ordinal);
}

public sealed record AiEstateProvider(
    string Id,
    string Name,
    /// <summary>external-api | gateway | local-runtime | local-inference | vector-store | observability | assistant.</summary>
    string Kind,
    IReadOnlyList<string> Packages,
    int EndpointFiles,
    IReadOnlyList<string> EnvVars,
    IReadOnlyList<string> Models,
    int CodeFiles,
    /// <summary>Null when no allowlist is configured; non-external kinds are always approved.</summary>
    bool? Approved)
{
    public bool IsExternal => Kind == "external-api";
}

public sealed record AiEstateFramework(string Name, IReadOnlyList<string> Packages);

public sealed record AiEstateMcpServer(string Config, string Name, string Transport, string Capability, bool Remote, string? Host, string? Command, string? Known, int Secrets);

public sealed record AiEstateRetiredModel(string Model, string? RetiredOn, string? Replacement, int Files);

/// <summary>
/// The persisted AI inventory of one repository — the <c>estateJson</c> the scanner stores in the
/// <c>ai.inventory</c> finding's data. Parsed tolerantly: a missing section is empty, never an error,
/// so a report can always be rendered from whatever an older scanner version wrote.
/// </summary>
public sealed record AiEstateRecord(
    string CatalogVersion,
    string CatalogHash,
    IReadOnlyList<AiEstateProvider> Providers,
    IReadOnlyList<AiEstateFramework> Frameworks,
    IReadOnlyList<AiEstateMcpServer> Mcp,
    IReadOnlyList<string> ActiveModels,
    IReadOnlyList<AiEstateRetiredModel> RetiredModels,
    IReadOnlyList<string> VectorStores,
    IReadOnlyList<string> LocalRuntimes,
    IReadOnlyList<string> Gateways,
    bool AllowlistConfigured,
    IReadOnlyList<string> Approved,
    IReadOnlyList<string> Unapproved,
    int SecretsInMcp)
{
    public IEnumerable<AiEstateProvider> ExternalProviders => Providers.Where(p => p.IsExternal);

    public int SdkPackages => Providers.Sum(p => p.Packages.Count);

    public int RemoteMcp => Mcp.Count(m => m.Remote);

    /// <summary>From the finding's data dictionary JSON (the shape FindingOccurrence.DataJson has).</summary>
    public static AiEstateRecord? Parse(string? occurrenceDataJson)
    {
        if (string.IsNullOrWhiteSpace(occurrenceDataJson))
        {
            return null;
        }

        try
        {
            using var data = JsonDocument.Parse(occurrenceDataJson);
            if (data.RootElement.ValueKind != JsonValueKind.Object || !data.RootElement.TryGetProperty("estateJson", out var estate) || estate.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return FromEstateJson(estate.GetString()!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static AiEstateRecord? FromEstateJson(string estateJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(estateJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var catalog = Obj(root, "catalog");
            var models = Obj(root, "models");
            var allowlist = Obj(root, "allowlist");
            return new AiEstateRecord(
                Str(catalog, "version") ?? "—",
                Str(catalog, "hash") ?? "—",
                Arr(root, "providers").Select(p => new AiEstateProvider(
                    Str(p, "id") ?? "?", Str(p, "name") ?? Str(p, "id") ?? "?", Str(p, "kind") ?? "external-api",
                    Strings(p, "packages"), Int(p, "endpointFiles"), Strings(p, "envVars"), Strings(p, "models"), Int(p, "codeFiles"), Bool(p, "approved"))).ToList(),
                Arr(root, "frameworks").Select(f => new AiEstateFramework(Str(f, "name") ?? "?", Strings(f, "packages"))).ToList(),
                Arr(root, "mcp").Select(m => new AiEstateMcpServer(
                    Str(m, "config") ?? "?", Str(m, "name") ?? "?", Str(m, "transport") ?? "unknown", Str(m, "capability") ?? "Unknown",
                    Bool(m, "remote") ?? false, Str(m, "host"), Str(m, "command"), Str(m, "known"), Int(m, "secrets"))).ToList(),
                Strings(models, "active"),
                Arr(models, "retired").Select(r => new AiEstateRetiredModel(Str(r, "model") ?? "?", Str(r, "retiredOn"), Str(r, "replacement"), Int(r, "files"))).ToList(),
                Strings(root, "vectorStores"),
                Strings(root, "localRuntimes"),
                Strings(root, "gateways"),
                Bool(allowlist, "configured") ?? false,
                Strings(allowlist, "approved"),
                Strings(allowlist, "unapproved"),
                Int(root, "secretsInMcp"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? Obj(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static IEnumerable<JsonElement> Arr(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
            : [];

    private static IReadOnlyList<string> Strings(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];

    private static string? Str(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static bool? Bool(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
