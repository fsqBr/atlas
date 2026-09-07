using System.Text.Json;

namespace Atlas.Scanner.Manifests;

/// <summary>One declared npm dependency and the section it came from.</summary>
public sealed record NpmDependency(string Name, string? Spec, string Section);

/// <summary>The single reader of package.json dependency sections (name level; lockfiles stay with the dependency scanner).</summary>
public static class NpmManifests
{
    public static readonly string[] Sections = ["dependencies", "devDependencies", "peerDependencies", "optionalDependencies"];

    public static IReadOnlyList<NpmDependency> ParsePackageJson(string json)
    {
        var deps = new List<NpmDependency>();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return deps;
            }

            foreach (var section in Sections)
            {
                if (document.RootElement.TryGetProperty(section, out var node) && node.ValueKind == JsonValueKind.Object)
                {
                    foreach (var dep in node.EnumerateObject())
                    {
                        deps.Add(new NpmDependency(dep.Name, dep.Value.ValueKind == JsonValueKind.String ? dep.Value.GetString() : null, section));
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return deps;
    }
}
