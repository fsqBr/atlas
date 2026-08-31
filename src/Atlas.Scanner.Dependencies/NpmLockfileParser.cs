using System.Text.Json;

namespace Atlas.Scanner.Dependencies;

/// <summary>One resolved npm package from a lockfile (the exact version that ships).</summary>
public sealed record NpmPackage(string Name, string Version, string LockfilePath, bool IsDev);

/// <summary>
/// Reads `package-lock.json` (lockfileVersion 1, 2 and 3) as data. Lockfiles list
/// the exact resolved versions, which is what vulnerability matching needs; the
/// looser ranges in package.json are ignored on purpose.
/// </summary>
public static class NpmLockfileParser
{
    public const string FileName = "package-lock.json";

    public static IReadOnlyList<NpmPackage> Parse(string lockfilePath, string json)
    {
        var result = new Dictionary<(string, string), NpmPackage>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            // v2/v3: "packages": { "": {...root...}, "node_modules/a": {...}, "node_modules/a/node_modules/b": {...} }
            if (root.TryGetProperty("packages", out var packages) && packages.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in packages.EnumerateObject())
                {
                    var marker = entry.Name.LastIndexOf("node_modules/", StringComparison.Ordinal);
                    if (marker < 0)
                    {
                        continue; // the root package or a workspace folder
                    }

                    var name = entry.Name[(marker + "node_modules/".Length)..];
                    if (entry.Value.TryGetProperty("name", out var realName) && realName.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(realName.GetString()))
                    {
                        name = realName.GetString()!; // aliased install ("safe-name": npm:vulnerable-pkg): match OSV by the real package
                    }

                    if (name.Length == 0 || !entry.Value.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var dev = entry.Value.TryGetProperty("dev", out var d) && d.ValueKind == JsonValueKind.True;
                    Add(result, new NpmPackage(name, version.GetString()!, lockfilePath, dev));
                }
            }

            // v1 (and v2 keeps it for compatibility): "dependencies": { "a": { "version": "...", "dependencies": {...} } }
            if (result.Count == 0 && root.TryGetProperty("dependencies", out var dependencies) && dependencies.ValueKind == JsonValueKind.Object)
            {
                Walk(dependencies, lockfilePath, result, depth: 0);
            }
        }

        return result.Values.OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Version, StringComparer.Ordinal).ToList();
    }

    private static void Walk(JsonElement dependencies, string lockfilePath, Dictionary<(string, string), NpmPackage> result, int depth)
    {
        if (depth > 32)
        {
            return;
        }

        foreach (var entry in dependencies.EnumerateObject())
        {
            if (entry.Value.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String)
            {
                var dev = entry.Value.TryGetProperty("dev", out var d) && d.ValueKind == JsonValueKind.True;
                Add(result, new NpmPackage(entry.Name, version.GetString()!, lockfilePath, dev));
            }

            if (entry.Value.TryGetProperty("dependencies", out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                Walk(nested, lockfilePath, result, depth + 1);
            }
        }
    }

    private static void Add(Dictionary<(string, string), NpmPackage> result, NpmPackage package)
    {
        var key = (package.Name.ToLowerInvariant(), package.Version);
        if (!result.TryGetValue(key, out var existing) || (existing.IsDev && !package.IsDev))
        {
            result[key] = package;
        }
    }
}
