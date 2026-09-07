using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.Scanner.Licenses;

public sealed record PackageLicense(string Ecosystem, string Id, string Version, string? Expression, string? Url, LicenseClass Class, string Source);

public interface ILicenseResolver
{
    Task<IReadOnlyList<PackageLicense>> ResolveAsync(IReadOnlyList<(string Ecosystem, string Id, string Version)> packages, CancellationToken cancellationToken);
}

public sealed class LicenseOptions
{
    public const string SectionName = "Atlas:Licenses";

    public bool Enabled { get; set; } = true;

    /// <summary>JSON cache shared across runs; the worker volume is the natural place (/var/atlas/workspaces/.licenses.json).</summary>
    public string? CachePath { get; set; }

    public int MaxLookupsPerRun { get; set; } = 400;

    public int Concurrency { get; set; } = 8;

    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>SPDX ids (or classes: StrongCopyleft, Restricted) the organisation forbids in shipped software → Critical findings.</summary>
    public List<string> Denied { get; set; } = [];

    public string NuGetBaseUrl { get; set; } = "https://api.nuget.org/v3-flatcontainer";

    public string NpmRegistryUrl { get; set; } = "https://registry.npmjs.org";
}

/// <summary>
/// License metadata from the public registries (nuspec for NuGet, packument for npm)
/// with a JSON file cache so a package is looked up once per installation. Read-only
/// egress to two well-known hosts; failures degrade to "unknown", never to a crash.
/// </summary>
public sealed class RegistryLicenseResolver(IHttpClientFactory httpClients, LicenseOptions options, ILogger<RegistryLicenseResolver>? logger = null) : ILicenseResolver
{
    public const string HttpClientName = "atlas-licenses";
    private readonly ILogger _logger = logger ?? NullLogger<RegistryLicenseResolver>.Instance;
    private static readonly SemaphoreSlim CacheLock = new(1, 1);

    public async Task<IReadOnlyList<PackageLicense>> ResolveAsync(IReadOnlyList<(string Ecosystem, string Id, string Version)> packages, CancellationToken cancellationToken)
    {
        var cache = await LoadCacheAsync(cancellationToken);
        var results = new ConcurrentBag<PackageLicense>();
        var pending = new List<(string Ecosystem, string Id, string Version)>();

        foreach (var p in packages.Distinct())
        {
            if (cache.TryGetValue(Key(p.Ecosystem, p.Id, p.Version), out var cached))
            {
                results.Add(cached with { Source = "cache" });
            }
            else
            {
                pending.Add(p);
            }
        }

        if (options.Enabled && pending.Count > 0)
        {
            var budget = pending.Take(options.MaxLookupsPerRun).ToList();
            var skipped = pending.Skip(options.MaxLookupsPerRun);
            var http = httpClients.CreateClient(HttpClientName);
            using var gate = new SemaphoreSlim(Math.Max(1, options.Concurrency));
            var fresh = new ConcurrentDictionary<string, PackageLicense>();
            await Task.WhenAll(budget.Select(async p =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var resolved = await LookupAsync(http, p.Ecosystem, p.Id, p.Version, cancellationToken);
                    results.Add(resolved);
                    if (resolved.Class != LicenseClass.Unknown || resolved.Expression is not null)
                    {
                        fresh[Key(p.Ecosystem, p.Id, p.Version)] = resolved;
                    }
                }
                finally
                {
                    gate.Release();
                }
            }));

            foreach (var p in skipped)
            {
                results.Add(new PackageLicense(p.Ecosystem, p.Id, p.Version, null, null, LicenseClass.Unknown, "not-looked-up"));
            }

            if (fresh.Count > 0)
            {
                await SaveCacheAsync(cache, fresh, cancellationToken);
            }
        }
        else
        {
            foreach (var p in pending)
            {
                results.Add(new PackageLicense(p.Ecosystem, p.Id, p.Version, null, null, LicenseClass.Unknown, options.Enabled ? "not-looked-up" : "disabled"));
            }
        }

        return results.OrderBy(r => r.Ecosystem).ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Version, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<PackageLicense> LookupAsync(HttpClient http, string ecosystem, string id, string version, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));
            if (ecosystem == "nuget")
            {
                var lower = id.ToLowerInvariant();
                var url = $"{options.NuGetBaseUrl.TrimEnd('/')}/{lower}/{version.ToLowerInvariant()}/{lower}.nuspec";
                using var response = await http.GetAsync(url, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    return new PackageLicense(ecosystem, id, version, null, null, LicenseClass.Unknown, $"nuget:{(int)response.StatusCode}");
                }

                return ParseNuspec(id, version, await response.Content.ReadAsStringAsync(timeout.Token));
            }

            if (ecosystem == "npm")
            {
                var url = $"{options.NpmRegistryUrl.TrimEnd('/')}/{Uri.EscapeDataString(id).Replace("%40", "@")}/{Uri.EscapeDataString(version)}";
                using var response = await http.GetAsync(url, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    return new PackageLicense(ecosystem, id, version, null, null, LicenseClass.Unknown, $"npm:{(int)response.StatusCode}");
                }

                return ParseNpm(id, version, await response.Content.ReadAsStringAsync(timeout.Token));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "License lookup failed for {Ecosystem}:{Id}@{Version}.", ecosystem, id, version);
        }

        return new PackageLicense(ecosystem, id, version, null, null, LicenseClass.Unknown, "error");
    }

    public static PackageLicense ParseNuspec(string id, string version, string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var metadata = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
            var license = metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "license");
            var licenseUrl = metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "licenseUrl")?.Value.Trim();
            string? expression = null;
            if (license is not null)
            {
                var type = license.Attribute("type")?.Value;
                expression = type == "expression" ? license.Value.Trim() : type == "file" ? "LicenseRef-File" : license.Value.Trim();
            }

            if (expression is null && licenseUrl is not null && licenseUrl.Contains("licenses.nuget.org/", StringComparison.OrdinalIgnoreCase))
            {
                expression = Uri.UnescapeDataString(licenseUrl[(licenseUrl.IndexOf("licenses.nuget.org/", StringComparison.OrdinalIgnoreCase) + "licenses.nuget.org/".Length)..].Trim('/'));
            }

            expression ??= LicenseClassifier.FromUrl(licenseUrl);
            return new PackageLicense("nuget", id, version, expression, licenseUrl, LicenseClassifier.Classify(expression), "nuget");
        }
        catch (System.Xml.XmlException)
        {
            return new PackageLicense("nuget", id, version, null, null, LicenseClass.Unknown, "nuget:invalid");
        }
    }

    public static PackageLicense ParseNpm(string id, string version, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            string? expression = null;
            if (doc.RootElement.TryGetProperty("license", out var lic))
            {
                expression = lic.ValueKind == JsonValueKind.String ? lic.GetString() : lic.ValueKind == JsonValueKind.Object && lic.TryGetProperty("type", out var t) ? t.GetString() : null;
            }
            else if (doc.RootElement.TryGetProperty("licenses", out var lics) && lics.ValueKind == JsonValueKind.Array)
            {
                expression = string.Join(" OR ", lics.EnumerateArray().Select(l => l.ValueKind == JsonValueKind.Object && l.TryGetProperty("type", out var t) ? t.GetString() : l.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)));
            }

            return new PackageLicense("npm", id, version, string.IsNullOrWhiteSpace(expression) ? null : expression, null, LicenseClassifier.Classify(expression), "npm");
        }
        catch (JsonException)
        {
            return new PackageLicense("npm", id, version, null, null, LicenseClass.Unknown, "npm:invalid");
        }
    }

    private static string Key(string ecosystem, string id, string version) => $"{ecosystem}:{id.ToLowerInvariant()}@{version.ToLowerInvariant()}";

    private async Task<Dictionary<string, PackageLicense>> LoadCacheAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.CachePath) || !File.Exists(options.CachePath))
        {
            return new Dictionary<string, PackageLicense>(StringComparer.Ordinal);
        }

        try
        {
            var json = await File.ReadAllTextAsync(options.CachePath, cancellationToken);
            return JsonSerializer.Deserialize<Dictionary<string, PackageLicense>>(json) ?? new Dictionary<string, PackageLicense>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "License cache at {Path} is unreadable; starting fresh.", options.CachePath);
            return new Dictionary<string, PackageLicense>(StringComparer.Ordinal);
        }
    }

    private async Task SaveCacheAsync(Dictionary<string, PackageLicense> cache, ConcurrentDictionary<string, PackageLicense> fresh, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.CachePath))
        {
            return;
        }

        await CacheLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var (key, value) in fresh)
            {
                cache[key] = value with { Source = "registry" };
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.CachePath))!);
            var tmp = options.CachePath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(cache), cancellationToken);
            File.Move(tmp, options.CachePath, overwrite: true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not persist the license cache to {Path}.", options.CachePath);
        }
        finally
        {
            CacheLock.Release();
        }
    }
}
