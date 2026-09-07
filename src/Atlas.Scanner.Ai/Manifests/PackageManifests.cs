using Atlas.Language.Abstractions;
using Atlas.Scanner.Manifests;

namespace Atlas.Scanner.Ai.Manifests;

/// <summary>A declared dependency: ecosystem, name, optional version, and the manifest that declares it.</summary>
public sealed record PackageFact(string Ecosystem, string Name, string? Version, string ManifestPath);

/// <summary>
/// Name-level extraction of declared packages across the four ecosystems Atlas analyzes. Deliberately
/// shallow (no resolution, no lockfiles): the AI scanner needs "which SDKs does this repository declare",
/// not versions to judge — the platform scanners keep owning version semantics. Every parser is
/// tolerant: a malformed manifest yields zero facts, never an exception.
/// </summary>
public static class PackageManifests
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

    public static IReadOnlyList<PackageFact> ParsePackageJson(string path, string json) =>
        NpmManifests.ParsePackageJson(json).Select(d => new PackageFact(Npm, d.Name, d.Spec, path)).ToList();

    public static IReadOnlyList<PackageFact> ParseRequirements(string path, string text) => FromPython(path, PythonManifests.ParseRequirements(path, text));

    public static IReadOnlyList<PackageFact> ParsePyProject(string path, string text) => FromPython(path, PythonManifests.ParsePyProject(path, text));

    public static IReadOnlyList<PackageFact> ParsePipfile(string path, string text) => FromPython(path, PythonManifests.ParsePipfile(path, text));

    public static IReadOnlyList<PackageFact> ParsePom(string path, string xml) =>
        JavaManifests.ParsePom(path, xml) is { } module ? FromJava(path, module) : [];

    public static IReadOnlyList<PackageFact> ParseGradle(string path, string text) => FromJava(path, JavaManifests.ParseGradle(path, text));

    /// <summary>PyPI names normalised the way the Python scanner and OSV spell them (lowercase, dashes).</summary>
    public static string Normalize(string pythonName) => PythonManifests.NormalizeName(pythonName);

    private static IReadOnlyList<PackageFact> FromPython(string path, PythonModule module) =>
        module.Dependencies.Select(d => new PackageFact(PyPi, d.Package, d.Pinned ? d.Version : null, path)).ToList();

    /// <summary>Maven coordinates as group:artifact; a version that is still a ${property} counts as unknown.</summary>
    private static IReadOnlyList<PackageFact> FromJava(string path, JavaModule module) =>
        module.Dependencies.Select(d => new PackageFact(Maven, $"{d.Group}:{d.Artifact}", d.Version is { Length: > 0 } v && !v.StartsWith('$') ? v : null, path)).ToList();
}
