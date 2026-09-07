using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Atlas.Scanner.Ai.Catalog;

// ---- Catalog document. Pure data: what the scanner recognizes, never how. ----

public sealed record ProviderSignature(
    string Id,
    string Name,
    /// <summary>external-api | gateway | local-runtime | vector-store | observability | assistant | local-inference.</summary>
    string Kind);

public sealed record PackageSignature(
    /// <summary>nuget | npm | pypi | maven (maven names are "groupId:artifactId"; a prefix may end at the colon).</summary>
    string Ecosystem,
    string Name,
    bool Prefix = false,
    string? Provider = null,
    /// <summary>provider-sdk | agent-framework | mcp-sdk | vector-store | abstraction | gateway | observability | local-inference | utility.</summary>
    string Category = "provider-sdk",
    string? Framework = null,
    /// <summary>Import/namespace roots that identify the package in source code (evidence without a manifest).</summary>
    IReadOnlyList<string>? Imports = null);

public sealed record PatternSignature(
    string Id,
    string Pattern,
    string? Provider,
    IReadOnlyList<string> Fixtures,
    /// <summary>Models only: the date the provider retired the model (ISO date) and the suggested replacement.</summary>
    string? RetiredOn = null,
    string? Replacement = null,
    string? Note = null);

public sealed record EnvVarSignature(string Name, string Provider);

public sealed record McpKnownServer(
    /// <summary>Case-insensitive substring matched against the server name, command, arguments and URL.</summary>
    string Match,
    string Name,
    /// <summary>Read | Write | ReadWrite | Execute | None.</summary>
    string Capability);

public sealed record McpSection(IReadOnlyList<string> ConfigFiles, IReadOnlyList<McpKnownServer> KnownServers);

public sealed record CatalogDocument(
    string Version,
    IReadOnlyList<ProviderSignature> Providers,
    IReadOnlyList<PackageSignature> Packages,
    IReadOnlyList<PatternSignature> Endpoints,
    IReadOnlyList<PatternSignature> Models,
    IReadOnlyList<EnvVarSignature> EnvVars,
    McpSection Mcp);

/// <summary>
/// The loaded, validated, compiled catalog. Every match records <see cref="Hash"/> so a finding can be
/// traced to the exact catalog that produced it ("effectiveCatalogHash").
/// </summary>
public sealed class SignatureCatalog
{
    public const string ResourceName = "Atlas.Scanner.Ai.ai-signatures.json";
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private SignatureCatalog(CatalogDocument document, string hash, string source)
    {
        Document = document;
        Hash = hash;
        Source = source;
        Providers = document.Providers.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        Endpoints = document.Endpoints.Select(e => (e, Compile(e.Pattern))).ToList();
        Models = document.Models.Select(m => (m, Compile(m.Pattern))).ToList();
        EnvVars = document.EnvVars.ToDictionary(v => v.Name, v => v, StringComparer.Ordinal);
        EnvVarPattern = document.EnvVars.Count == 0
            ? null
            : new Regex(@"\b(" + string.Join('|', document.EnvVars.Select(v => Regex.Escape(v.Name)).OrderByDescending(n => n.Length)) + @")\b",
                RegexOptions.CultureInvariant | RegexOptions.Compiled, MatchTimeout);
    }

    public CatalogDocument Document { get; }

    /// <summary>SHA-256 (hex, lower) of the catalog bytes as loaded.</summary>
    public string Hash { get; }

    /// <summary>"builtin" or the override file path.</summary>
    public string Source { get; }

    public string Version => Document.Version;

    public IReadOnlyDictionary<string, ProviderSignature> Providers { get; }

    public IReadOnlyList<(PatternSignature Signature, Regex Regex)> Endpoints { get; }

    public IReadOnlyList<(PatternSignature Signature, Regex Regex)> Models { get; }

    public IReadOnlyDictionary<string, EnvVarSignature> EnvVars { get; }

    public Regex? EnvVarPattern { get; }

    public static SignatureCatalog LoadBuiltin()
    {
        using var stream = typeof(SignatureCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded catalog '{ResourceName}' is missing from the assembly.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Load(ms.ToArray(), "builtin");
    }

    public static SignatureCatalog LoadFile(string path) => Load(File.ReadAllBytes(path), path);

    public static SignatureCatalog Load(byte[] bytes, string source)
    {
        var document = JsonSerializer.Deserialize<CatalogDocument>(bytes, JsonOptions)
            ?? throw new InvalidOperationException("The signature catalog is empty.");
        var errors = Validate(document);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid signature catalog: " + string.Join("; ", errors));
        }

        return new SignatureCatalog(document, Convert.ToHexStringLower(SHA256.HashData(bytes)), source);
    }

    /// <summary>Structural contract every catalog (builtin or custom) must satisfy; the test suite runs it against the builtin one.</summary>
    public static IReadOnlyList<string> Validate(CatalogDocument document)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(document.Version) || !System.Version.TryParse(document.Version, out _))
        {
            errors.Add("version must be a dotted version (e.g. 1.0.0)");
        }

        var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in document.Providers ?? [])
        {
            if (string.IsNullOrWhiteSpace(provider.Id) || !providers.Add(provider.Id))
            {
                errors.Add($"provider id missing or duplicated: '{provider.Id}'");
            }

            if (string.IsNullOrWhiteSpace(provider.Name) || string.IsNullOrWhiteSpace(provider.Kind))
            {
                errors.Add($"provider '{provider.Id}' needs name and kind");
            }
        }

        var packageKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in document.Packages ?? [])
        {
            if (package.Ecosystem is not ("nuget" or "npm" or "pypi" or "maven"))
            {
                errors.Add($"package '{package.Name}': unknown ecosystem '{package.Ecosystem}'");
            }

            if (string.IsNullOrWhiteSpace(package.Name) || !packageKeys.Add($"{package.Ecosystem}:{package.Name}"))
            {
                errors.Add($"package name missing or duplicated: '{package.Ecosystem}:{package.Name}'");
            }

            if (package.Provider is not null && !providers.Contains(package.Provider))
            {
                errors.Add($"package '{package.Name}' references unknown provider '{package.Provider}'");
            }

            if (package.Category == "agent-framework" && string.IsNullOrWhiteSpace(package.Framework))
            {
                errors.Add($"agent framework package '{package.Name}' needs a framework display name");
            }
        }

        ValidatePatterns(document.Endpoints, "endpoint", providers, errors, requireProvider: true);
        ValidatePatterns(document.Models, "model", providers, errors, requireProvider: true);

        var envNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var env in document.EnvVars ?? [])
        {
            if (string.IsNullOrWhiteSpace(env.Name) || !envNames.Add(env.Name))
            {
                errors.Add($"env var missing or duplicated: '{env.Name}'");
            }

            if (!providers.Contains(env.Provider))
            {
                errors.Add($"env var '{env.Name}' references unknown provider '{env.Provider}'");
            }
        }

        if (document.Mcp is null || document.Mcp.ConfigFiles is not { Count: > 0 })
        {
            errors.Add("mcp.configFiles must list at least one file name");
        }
        else
        {
            foreach (var server in document.Mcp.KnownServers ?? [])
            {
                if (server.Capability is not ("Read" or "Write" or "ReadWrite" or "Execute" or "None"))
                {
                    errors.Add($"mcp known server '{server.Name}': capability must be Read, Write, ReadWrite, Execute or None");
                }
            }
        }

        return errors;
    }

    private static void ValidatePatterns(IReadOnlyList<PatternSignature>? patterns, string kind, HashSet<string> providers, List<string> errors, bool requireProvider)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var signature in patterns ?? [])
        {
            if (string.IsNullOrWhiteSpace(signature.Id) || !ids.Add(signature.Id))
            {
                errors.Add($"{kind} id missing or duplicated: '{signature.Id}'");
            }

            if (requireProvider && (signature.Provider is null || !providers.Contains(signature.Provider)))
            {
                errors.Add($"{kind} '{signature.Id}' references unknown provider '{signature.Provider}'");
            }

            Regex regex;
            try
            {
                regex = Compile(signature.Pattern);
            }
            catch (ArgumentException ex)
            {
                errors.Add($"{kind} '{signature.Id}': invalid regex ({ex.Message})");
                continue;
            }

            // Fixtures are the catalog's own regression suite: a signature without a positive example cannot be trusted.
            if (signature.Fixtures is not { Count: > 0 })
            {
                errors.Add($"{kind} '{signature.Id}' has no fixtures");
                continue;
            }

            foreach (var fixture in signature.Fixtures.Where(f => !regex.IsMatch(f)))
            {
                errors.Add($"{kind} '{signature.Id}' does not match its fixture '{fixture}'");
            }

            if (signature.RetiredOn is not null && !DateOnly.TryParseExact(signature.RetiredOn, "yyyy-MM-dd", out _))
            {
                errors.Add($"{kind} '{signature.Id}': retiredOn must be yyyy-MM-dd");
            }
        }
    }

    private static Regex Compile(string pattern) =>
        new(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled, MatchTimeout);

    public ProviderSignature? Provider(string? id) =>
        id is not null && Providers.TryGetValue(id, out var provider) ? provider : null;

    /// <summary>Exact or prefix package match within an ecosystem; the longest signature wins.</summary>
    public PackageSignature? MatchPackage(string ecosystem, string name)
    {
        PackageSignature? best = null;
        foreach (var signature in Document.Packages)
        {
            if (!string.Equals(signature.Ecosystem, ecosystem, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var hit = signature.Prefix
                ? name.StartsWith(signature.Name, StringComparison.OrdinalIgnoreCase)
                : string.Equals(signature.Name, name, StringComparison.OrdinalIgnoreCase);
            if (hit && (best is null || signature.Name.Length > best.Name.Length))
            {
                best = signature;
            }
        }

        return best;
    }

    /// <summary>A source-code import (namespace root, module or specifier) mapped to the package that owns it.</summary>
    public PackageSignature? MatchImport(string ecosystem, string import)
    {
        PackageSignature? best = null;
        foreach (var signature in Document.Packages)
        {
            if (!string.Equals(signature.Ecosystem, ecosystem, StringComparison.OrdinalIgnoreCase) || signature.Imports is null)
            {
                continue;
            }

            foreach (var root in signature.Imports)
            {
                var hit = string.Equals(import, root, StringComparison.OrdinalIgnoreCase)
                    || import.StartsWith(root + ".", StringComparison.OrdinalIgnoreCase)
                    || import.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
                if (hit && (best is null || root.Length > (best.Imports?.Max(r => r.Length) ?? 0)))
                {
                    best = signature;
                }
            }
        }

        return best;
    }
}
