using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Atlas.Language.Abstractions;

namespace Atlas.Scanner.Ai.Manifests;

/// <summary>A declared dependency: ecosystem, name, optional version, and the manifest that declares it.</summary>
public sealed record PackageFact(string Ecosystem, string Name, string? Version, string ManifestPath);

/// <summary>
/// Name-level extraction of declared packages across the four ecosystems Atlas analyzes. Deliberately
/// shallow (no resolution, no lockfiles): the AI scanner needs "which SDKs does this repository declare",
/// not versions to judge — the platform scanners keep owning version semantics. Every parser is
/// tolerant: a malformed manifest yields zero facts, never an exception.
/// </summary>
public static partial class PackageManifests
{
    public const string NuGet = "nuget";
    public const string Npm = "npm";
    public const string PyPi = "pypi";
    public const string Maven = "maven";

    /// <summary>File name patterns the scanner asks the workspace for, mapped to the parser that reads them.</summary>
    public static readonly (string Pattern, Func<string, string, IReadOnlyList<PackageFact>> Parse)[] Manifests =
    [
        ("package.json", ParsePackageJson),
        ("requirements*.txt", ParseRequirements),
        ("pyproject.toml", ParsePyProject),
        ("Pipfile", ParsePipfile),
        ("pom.xml", ParsePom),
        ("build.gradle", ParseGradle),
        ("build.gradle.kts", ParseGradle),
    ];

    /// <summary>NuGet packages come from the C#/VB analyzers' project facts — no second csproj parser.</summary>
    public static IEnumerable<PackageFact> FromProjects(IReadOnlyDictionary<string, LanguageAnalysisResult> languages) =>
        languages.Values
            .SelectMany(l => l.Projects)
            .SelectMany(p => p.PackageReferences.Select(r => new PackageFact(NuGet, r.Id, r.Version, p.RelativePath)));

    public static IReadOnlyList<PackageFact> ParsePackageJson(string path, string json)
    {
        var facts = new List<PackageFact>();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return facts;
            }

            foreach (var section in new[] { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" })
            {
                if (document.RootElement.TryGetProperty(section, out var deps) && deps.ValueKind == JsonValueKind.Object)
                {
                    foreach (var dep in deps.EnumerateObject())
                    {
                        facts.Add(new PackageFact(Npm, dep.Name, dep.Value.ValueKind == JsonValueKind.String ? dep.Value.GetString() : null, path));
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return facts;
    }

    [GeneratedRegex(@"^\s*([A-Za-z0-9][A-Za-z0-9._-]*)\s*(?:\[[^\]]*\])?\s*(?:(==|>=|<=|~=|!=|>|<|===)\s*([A-Za-z0-9.*+!-]+))?", RegexOptions.CultureInvariant)]
    private static partial Regex RequirementLine();

    public static IReadOnlyList<PackageFact> ParseRequirements(string path, string text)
    {
        var facts = new List<PackageFact>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var comment = line.IndexOf('#');
            if (comment >= 0)
            {
                line = line[..comment].Trim();
            }

            if (line.Length == 0 || line.StartsWith('-') || line.Contains("://", StringComparison.Ordinal) || line.StartsWith('.') || line.StartsWith('/'))
            {
                continue; // options (-r, -e, --index-url), URLs and local paths declare no package name
            }

            var m = RequirementLine().Match(line);
            if (m.Success)
            {
                facts.Add(new PackageFact(PyPi, Normalize(m.Groups[1].Value), m.Groups[2].Value == "==" ? m.Groups[3].Value : null, path));
            }
        }

        return facts;
    }

    [GeneratedRegex(@"^\s*\[(?<table>[^\]]+)\]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TomlTable();

    [GeneratedRegex(@"^\s*""?(?<name>[A-Za-z0-9][A-Za-z0-9._-]*)""?\s*=\s*(?:""(?<spec>[^""]*)""|\{)", RegexOptions.CultureInvariant)]
    private static partial Regex TomlKeyValue();

    [GeneratedRegex(@"""\s*(?<name>[A-Za-z][A-Za-z0-9._-]*)\s*(?:\[[^\]]*\])?\s*(?:(?<op>==|>=|<=|~=|!=|>|<)\s*(?<ver>[A-Za-z0-9.*+!-]+))?[^""]*""", RegexOptions.CultureInvariant)]
    private static partial Regex Pep621Entry();

    /// <summary>PEP 621 <c>[project] dependencies</c> / optional-dependencies arrays and Poetry <c>[tool.poetry.*dependencies]</c> tables.</summary>
    public static IReadOnlyList<PackageFact> ParsePyProject(string path, string text)
    {
        var facts = new List<PackageFact>();
        string? table = null;
        var inArray = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var tableMatch = TomlTable().Match(line);
            if (tableMatch.Success)
            {
                table = tableMatch.Groups["table"].Value.Trim();
                inArray = false;
                continue;
            }

            if (table is null)
            {
                continue;
            }

            var isPoetryDeps = table.StartsWith("tool.poetry.", StringComparison.Ordinal) && table.EndsWith("dependencies", StringComparison.Ordinal)
                || table.StartsWith("tool.poetry.group.", StringComparison.Ordinal) && table.EndsWith(".dependencies", StringComparison.Ordinal);
            if (isPoetryDeps)
            {
                var kv = TomlKeyValue().Match(line);
                if (kv.Success && kv.Groups["name"].Value != "python")
                {
                    facts.Add(new PackageFact(PyPi, Normalize(kv.Groups["name"].Value), null, path));
                }

                continue;
            }

            var isPep621 = table == "project" || table == "project.optional-dependencies" || table == "dependency-groups";
            if (!isPep621)
            {
                continue;
            }

            if (!inArray)
            {
                var trimmed = line.TrimStart();
                var isDepsKey = table == "project"
                    ? trimmed.StartsWith("dependencies", StringComparison.Ordinal) && trimmed.Contains('[')
                    : trimmed.Contains('[');
                if (!isDepsKey)
                {
                    continue;
                }

                inArray = true;
            }

            foreach (Match entry in Pep621Entry().Matches(line))
            {
                facts.Add(new PackageFact(PyPi, Normalize(entry.Groups["name"].Value), entry.Groups["op"].Value == "==" ? entry.Groups["ver"].Value : null, path));
            }

            if (line.Contains(']'))
            {
                inArray = false;
            }
        }

        return facts;
    }

    public static IReadOnlyList<PackageFact> ParsePipfile(string path, string text)
    {
        var facts = new List<PackageFact>();
        string? table = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var tableMatch = TomlTable().Match(line);
            if (tableMatch.Success)
            {
                table = tableMatch.Groups["table"].Value.Trim();
                continue;
            }

            if (table is "packages" or "dev-packages")
            {
                var kv = TomlKeyValue().Match(line);
                if (kv.Success)
                {
                    var spec = kv.Groups["spec"].Value;
                    facts.Add(new PackageFact(PyPi, Normalize(kv.Groups["name"].Value), spec.StartsWith("==", StringComparison.Ordinal) ? spec[2..] : null, path));
                }
            }
        }

        return facts;
    }

    public static IReadOnlyList<PackageFact> ParsePom(string path, string xml)
    {
        var facts = new List<PackageFact>();
        try
        {
            var doc = XDocument.Parse(xml, LoadOptions.None);
            foreach (var dep in doc.Descendants().Where(e => e.Name.LocalName == "dependency"))
            {
                var group = dep.Elements().FirstOrDefault(e => e.Name.LocalName == "groupId")?.Value.Trim();
                var artifact = dep.Elements().FirstOrDefault(e => e.Name.LocalName == "artifactId")?.Value.Trim();
                var version = dep.Elements().FirstOrDefault(e => e.Name.LocalName == "version")?.Value.Trim();
                if (!string.IsNullOrEmpty(group) && !string.IsNullOrEmpty(artifact))
                {
                    facts.Add(new PackageFact(Maven, $"{group}:{artifact}", version is { Length: > 0 } && !version.StartsWith('$') ? version : null, path));
                }
            }
        }
        catch (System.Xml.XmlException)
        {
        }

        return facts;
    }

    [GeneratedRegex(@"(?:implementation|api|compileOnly|runtimeOnly|testImplementation|annotationProcessor|classpath|kapt|ksp)\s*\(?\s*(?:platform\()?\s*['""](?<group>[A-Za-z0-9_.-]+):(?<artifact>[A-Za-z0-9_.-]+)(?::(?<version>[^'""]*))?['""]", RegexOptions.CultureInvariant)]
    private static partial Regex GradleCoordinate();

    public static IReadOnlyList<PackageFact> ParseGradle(string path, string text)
    {
        var facts = new List<PackageFact>();
        foreach (Match m in GradleCoordinate().Matches(text))
        {
            var version = m.Groups["version"].Value;
            facts.Add(new PackageFact(Maven, $"{m.Groups["group"].Value}:{m.Groups["artifact"].Value}", version.Length > 0 && !version.StartsWith('$') ? version : null, path));
        }

        return facts;
    }

    /// <summary>PEP 503 normalization: case-insensitive, runs of [-_.] collapse to '-'.</summary>
    public static string Normalize(string pythonName) =>
        Regex.Replace(pythonName.Trim(), "[-_.]+", "-").ToLowerInvariant();
}
