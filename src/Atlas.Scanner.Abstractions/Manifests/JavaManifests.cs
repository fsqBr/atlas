using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Atlas.Scanner.Manifests;

/// <summary>One Maven or Gradle module: its declared JDK (raw text, e.g. "1.8", "17") and group:artifact:version dependencies.</summary>
public sealed record JavaModule(string Path, string Name, string? Jdk, IReadOnlyList<(string Group, string Artifact, string? Version)> Dependencies);

/// <summary>
/// The single reader of Java build manifests, shared by the Java platform scanner (JDK, EOL frameworks, CVEs) and the
/// AI Estate scanner (names). Maven properties are resolved one level; Gradle comments are stripped first so a
/// commented-out coordinate never becomes a dependency.
/// </summary>
public static partial class JavaManifests
{
    public static JavaModule? ParsePom(string path, string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            return null;
        }

        var root = doc.Root;
        if (root is null || root.Name.LocalName != "project")
        {
            return null;
        }

        static XElement? Child(XElement e, string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name);
        var properties = Child(root, "properties")?.Elements()
            .GroupBy(x => x.Name.LocalName).ToDictionary(g => g.Key, g => g.First().Value.Trim())
            ?? new Dictionary<string, string>();
        string? Resolve(string? value) =>
            value is not null && value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith('}')
                ? properties.GetValueOrDefault(value[2..^1])
                : value;

        var jdk = properties.GetValueOrDefault("java.version")
            ?? properties.GetValueOrDefault("maven.compiler.release")
            ?? properties.GetValueOrDefault("maven.compiler.source")
            ?? properties.GetValueOrDefault("maven.compiler.target");

        var dependencies = new List<(string, string, string?)>();
        if (Child(root, "parent") is { } parent
            && Child(parent, "groupId")?.Value.Trim() is { } parentGroup
            && Child(parent, "artifactId")?.Value.Trim() is { } parentArtifact)
        {
            dependencies.Add((parentGroup, parentArtifact, Resolve(Child(parent, "version")?.Value.Trim())));
        }

        foreach (var dependency in Child(root, "dependencies")?.Elements().Where(x => x.Name.LocalName == "dependency") ?? [])
        {
            var group = Child(dependency, "groupId")?.Value.Trim();
            var artifact = Child(dependency, "artifactId")?.Value.Trim();
            if (group is not null && artifact is not null)
            {
                dependencies.Add((group, artifact, Resolve(Child(dependency, "version")?.Value.Trim())));
            }
        }

        var name = Child(root, "artifactId")?.Value.Trim();
        if (string.IsNullOrEmpty(name))
        {
            name = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path.Replace('\\', '/')));
        }

        return new JavaModule(path, string.IsNullOrEmpty(name) ? "maven-module" : name!, jdk, dependencies);
    }

    public static JavaModule ParseGradle(string path, string text)
    {
        // Comments are not dependencies: "// TODO drop log4j:log4j:1.2.17" must not flag anything.
        text = GradleBlockCommentRegex().Replace(text, " ");
        text = GradleLineCommentRegex().Replace(text, " ");

        var dependencies = GradleDependencyRegex().Matches(text)
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value, (string?)m.Groups[3].Value))
            .ToList();

        // JavaVersion.VERSION_1_8 must win over the plain sourceCompatibility number, or the
        // most common legacy spelling parses as "JDK 1".
        string? jdk = null;
        if (GradleJavaVersionRegex().Match(text) is { Success: true } jv)
        {
            jdk = jv.Groups[2].Success ? jv.Groups[2].Value : jv.Groups[1].Value;
        }
        else if (GradleSourceCompatibilityRegex().Match(text) is { Success: true } sc)
        {
            jdk = sc.Groups[1].Value;
        }
        else if (GradleToolchainRegex().Match(text) is { Success: true } tc)
        {
            jdk = tc.Groups[1].Value;
        }

        var directory = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path.Replace('\\', '/')));
        return new JavaModule(path, string.IsNullOrEmpty(directory) ? "gradle-module" : directory!, jdk, dependencies);
    }

    [GeneratedRegex(@"[""']([A-Za-z0-9_.\-]+):([A-Za-z0-9_.\-]+):([A-Za-z0-9_.\-+]+)[""']")]
    private static partial Regex GradleDependencyRegex();

    [GeneratedRegex(@"sourceCompatibility\s*=?\s*[""']?(1\.\d+|\d+)")]
    private static partial Regex GradleSourceCompatibilityRegex();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex GradleBlockCommentRegex();

    [GeneratedRegex(@"//[^\n]*")]
    private static partial Regex GradleLineCommentRegex();

    [GeneratedRegex(@"JavaVersion\.VERSION_(\d+)(?:_(\d+))?")]
    private static partial Regex GradleJavaVersionRegex();

    [GeneratedRegex(@"JavaLanguageVersion\.of\(\s*(\d+)\s*\)")]
    private static partial Regex GradleToolchainRegex();
}
