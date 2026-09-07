using System.Text.RegularExpressions;

namespace Atlas.Scanner.Manifests;

/// <summary>One declared Python dependency: PyPI-normalised name, the version when one is written, and whether it is pinned (==).</summary>
public sealed record PythonDependency(string Package, string? Version, bool Pinned);

/// <summary>One Python manifest: requirements*.txt, pyproject.toml, Pipfile or setup.py.</summary>
public sealed record PythonModule(string Path, string Name, string? PythonRequires, IReadOnlyList<PythonDependency> Dependencies);

/// <summary>
/// The single reader of Python manifests, shared by the Python platform scanner (versions, pins, interpreter floors)
/// and the AI Estate scanner (names). Tolerant: a malformed manifest yields an empty module, never an exception.
/// </summary>
public static partial class PythonManifests
{
    /// <summary>Manifest file names and the parser that reads each.</summary>
    public static readonly (string FileName, Func<string, string, PythonModule> Parse)[] Manifests =
    [
        ("requirements.txt", ParseRequirements),
        ("pyproject.toml", ParsePyProject),
        ("Pipfile", ParsePipfile),
        ("setup.py", ParseSetupPy),
    ];

    public static PythonModule ParseRequirements(string path, string text)
    {
        var dependencies = new List<PythonDependency>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            var hash = line.IndexOf('#');
            if (hash >= 0)
            {
                line = line[..hash].Trim();
            }

            if (line.Length == 0 || line.StartsWith('-') || line.Contains("://", StringComparison.Ordinal) || line.StartsWith('.') || line.StartsWith('/'))
            {
                continue; // options (-r, -e, --index-url…), URLs and local paths are not packages
            }

            if (RequirementRegex().Match(line) is { Success: true } m)
            {
                var (version, pinned) = InterpretSpecifier(m.Groups[2].Success ? m.Groups[2].Value : null, m.Groups[3].Success ? m.Groups[3].Value : null);
                dependencies.Add(new PythonDependency(NormalizeName(m.Groups[1].Value), version, pinned));
            }
        }

        return new PythonModule(path, ModuleName(path), null, dependencies);
    }

    public static PythonModule ParsePyProject(string path, string text)
    {
        var requires = PyProjectRequiresRegex().Match(text) is { Success: true } r ? r.Groups[1].Value : null;
        var dependencies = new List<PythonDependency>();

        // PEP 621: only strings INSIDE dependency arrays are requirements — pyproject.toml is full
        // of other quoted strings (name, license, urls) that must never become "packages".
        foreach (Match block in DependencyArrayRegex().Matches(text))
        {
            AddQuotedRequirements(dependencies, block.Groups[1].Value);
        }

        if (OptionalDependenciesSectionRegex().Match(text) is { Success: true } optional)
        {
            foreach (Match array in InnerArrayRegex().Matches(optional.Groups[1].Value))
            {
                AddQuotedRequirements(dependencies, array.Groups[1].Value);
            }
        }

        // Poetry: bare TOML keys under [tool.poetry.dependencies] (and group/dev variants);
        // the "python" key is the interpreter constraint, not a package.
        foreach (Match section in PoetrySectionRegex().Matches(text))
        {
            foreach (Match dep in PoetryDependencyRegex().Matches(section.Groups[1].Value))
            {
                var name = dep.Groups[1].Value;
                var value = dep.Groups[2].Success ? dep.Groups[2].Value : dep.Groups[3].Value;
                if (name.Equals("python", StringComparison.OrdinalIgnoreCase))
                {
                    requires ??= value;
                    continue;
                }

                var (version, pinned) = InterpretValue(value);
                dependencies.Add(new PythonDependency(NormalizeName(name), version, pinned));
            }
        }

        return new PythonModule(path, ModuleName(path), requires, dependencies);
    }

    public static PythonModule ParsePipfile(string path, string text)
    {
        var requires = PipfilePythonRegex().Match(text) is { Success: true } r ? ">=" + r.Groups[1].Value : null;
        var dependencies = new List<PythonDependency>();
        foreach (Match m in PipfileDependencyRegex().Matches(text))
        {
            var name = m.Groups[1].Value;
            if (name.Equals("python_version", StringComparison.OrdinalIgnoreCase) || name.Equals("python_full_version", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            var (version, pinned) = InterpretValue(value);
            dependencies.Add(new PythonDependency(NormalizeName(name), version, pinned));
        }

        return new PythonModule(path, ModuleName(path), requires, dependencies);
    }

    public static PythonModule ParseSetupPy(string path, string text)
    {
        var requires = SetupPythonRequiresRegex().Match(text) is { Success: true } r ? r.Groups[1].Value : null;
        var dependencies = new List<PythonDependency>();
        foreach (Match block in SetupRequiresListRegex().Matches(text))
        {
            AddQuotedRequirements(dependencies, block.Groups[1].Value);
        }

        if (SetupExtrasRegex().Match(text) is { Success: true } extras)
        {
            AddQuotedRequirements(dependencies, extras.Groups[1].Value);
        }

        return new PythonModule(path, ModuleName(path), requires, dependencies);
    }

    private static void AddQuotedRequirements(List<PythonDependency> dependencies, string block)
    {
        foreach (Match m in QuotedRequirementRegex().Matches(block))
        {
            var name = m.Groups[1].Value;
            if (name.Equals("python", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var (version, pinned) = InterpretSpecifier(m.Groups[2].Success ? m.Groups[2].Value : null, m.Groups[3].Success ? m.Groups[3].Value : null);
            dependencies.Add(new PythonDependency(NormalizeName(name), version, pinned));
        }
    }

    /// <summary>An explicit specifier operator + version: "==" pins; ranges only provide a floor for the EOL gates.</summary>
    public static (string? Version, bool Pinned) InterpretSpecifier(string? op, string? version) =>
        version is null ? (null, false) : (version, op is "==" or "===");

    /// <summary>A Poetry/Pipfile value string: "1.2.3"/"==1.2.3" pin; "^1.11"/"&gt;=1.0"/"~1.2" floor; "*" nothing.</summary>
    public static (string? Version, bool Pinned) InterpretValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "*")
        {
            return (null, false);
        }

        var m = ValueSpecifierRegex().Match(value.Trim());
        if (!m.Success)
        {
            return (null, false);
        }

        var op = m.Groups[1].Value;
        var version = m.Groups[2].Value;
        return (version, op.Length == 0 || op is "==" or "===");
    }

    public static string ModuleName(string path)
    {
        var directory = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path.Replace('\\', '/')));
        return string.IsNullOrEmpty(directory) ? "python-app" : directory!;
    }

    /// <summary>PyPI names are case-insensitive with '-'/'_'/'.' equivalent; OSV uses the lowercase dashed form.</summary>
    public static string NormalizeName(string package) => Regex.Replace(package.Trim(), "[-_.]+", "-").ToLowerInvariant();

    [GeneratedRegex(@"^([A-Za-z0-9_.\-]+)(?:\[[^\]]*\])?\s*(?:(===?|>=|~=|<=?|>|!=)\s*([0-9][A-Za-z0-9_.\-*]*))?", RegexOptions.None, 2000)]
    private static partial Regex RequirementRegex();

    [GeneratedRegex(@"[""']([A-Za-z0-9_.\-]+)(?:\[[^\]]*\])?\s*(?:(===?|>=|~=|<=?|>|!=)\s*([0-9][A-Za-z0-9_.\-*]*))?[^""']*[""']", RegexOptions.None, 2000)]
    private static partial Regex QuotedRequirementRegex();

    [GeneratedRegex(@"(?s)[\w.\-]*dependencies[\w.\-]*\s*=\s*\[(.*?)\]", RegexOptions.None, 2000)]
    private static partial Regex DependencyArrayRegex();

    [GeneratedRegex(@"(?sm)^\[project\.optional-dependencies\]\s*(.*?)(?=^\[|\z)", RegexOptions.None, 2000)]
    private static partial Regex OptionalDependenciesSectionRegex();

    [GeneratedRegex(@"(?s)\[(.*?)\]", RegexOptions.None, 2000)]
    private static partial Regex InnerArrayRegex();

    [GeneratedRegex(@"(?sm)^\[tool\.poetry(?:\.group\.[\w\-]+)?\.(?:dev-)?dependencies\]\s*(.*?)(?=^\[|\z)", RegexOptions.None, 2000)]
    private static partial Regex PoetrySectionRegex();

    [GeneratedRegex(@"(?m)^([A-Za-z0-9_.\-]+)\s*=\s*(?:[""']([^""']*)[""']|\{[^}\n]*version\s*=\s*[""']([^""']+)[""'][^}\n]*\})", RegexOptions.None, 2000)]
    private static partial Regex PoetryDependencyRegex();

    [GeneratedRegex(@"(?s)(?:install_requires|tests_require|setup_requires)\s*=\s*\[(.*?)\]", RegexOptions.None, 2000)]
    private static partial Regex SetupRequiresListRegex();

    [GeneratedRegex(@"(?s)extras_require\s*=\s*\{(.*?)\}", RegexOptions.None, 2000)]
    private static partial Regex SetupExtrasRegex();

    [GeneratedRegex(@"requires-python\s*=\s*[""']([^""']+)[""']", RegexOptions.None, 2000)]
    private static partial Regex PyProjectRequiresRegex();

    [GeneratedRegex(@"python_requires\s*=\s*[""']([^""']+)[""']", RegexOptions.None, 2000)]
    private static partial Regex SetupPythonRequiresRegex();

    [GeneratedRegex(@"(?m)^python_version\s*=\s*[""']([^""']+)[""']", RegexOptions.None, 2000)]
    private static partial Regex PipfilePythonRegex();

    [GeneratedRegex(@"(?m)^([A-Za-z0-9_.\-]+)\s*=\s*(?:[""']([^""']*)[""']|\{[^}\n]*version\s*=\s*[""']([^""']+)[""'][^}\n]*\})", RegexOptions.None, 2000)]
    private static partial Regex PipfileDependencyRegex();

    [GeneratedRegex(@"^(===?|>=|~=|<=?|>|!=|\^|~)?\s*v?([0-9][A-Za-z0-9_.\-*]*)", RegexOptions.None, 2000)]
    private static partial Regex ValueSpecifierRegex();
}
