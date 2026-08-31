using System.Globalization;
using System.Text.RegularExpressions;
using Atlas.Domain.Findings;
using Atlas.Domain.Rules;
using Atlas.Domain.Workspaces;
using Atlas.Scanner.Abstractions;
using Atlas.Scanner.Dependencies.Vulnerabilities;

namespace Atlas.Scanner.Python;

/// <summary>
/// Python platform footprint (the project system as data): requirements/pyproject (PEP 621 and
/// Poetry)/Pipfile/setup.py packages, interpreter targets and their support status, end-of-life
/// frameworks, and known vulnerabilities from the offline OSV bundle when it carries the PyPI
/// ecosystem (add the PyPI export to Atlas:Vulnerabilities:SyncUrls to enable it). Manifests only
/// — no resolver, no execution; only pinned (==) versions are judged against OSV, while range
/// floors ("&gt;=1.11", "^1.11") still gate the end-of-life framework table.
/// </summary>
public sealed partial class PythonScanner(IVulnerabilitySource vulnerabilities) : IScanner
{
    public static class RuleIds
    {
        public const string Inventory = "python.inventory";
        public const string InterpreterEol = "python.interpreter.eol";
        public const string LegacyFramework = "python.legacy-framework";
        public const string VulnerablePackage = "python.vulnerable-dependency";
    }

    private const string RulesVersion = "1.0.0";
    private const string Pt = "pt-BR";
    private const int MaxVulnerabilityFindings = 100;

    private static IReadOnlyDictionary<string, RuleLocalization> Loc(RuleLocalization pt) =>
        new Dictionary<string, RuleLocalization> { [Pt] = pt };

    /// <summary>Normalized package name → display; BelowMajor flags only smaller majors (null = any version). First matching row wins per package.</summary>
    private static readonly (string Package, string Name, int? BelowMajor, Severity Severity, string Reason)[] LegacyFrameworks =
    [
        ("django", "Django 1.x", 2, Severity.High, "end of life since 2020; no security fixes"),
        ("django", "Django 2.x", 3, Severity.Medium, "end of extended support; upgrade to a maintained LTS (4.2+)"),
        ("flask", "Flask 0.x/1.x", 2, Severity.Medium, "out of support; Flask 2+ requires minor but real migration"),
        ("celery", "Celery 4.x or older", 5, Severity.Medium, "out of support"),
        ("tornado", "Tornado 5.x or older", 6, Severity.Medium, "out of support"),
        ("nose", "nose", null, Severity.Medium, "unmaintained since 2015; migrate the test suite to pytest"),
        ("pycrypto", "PyCrypto", null, Severity.High, "abandoned with known CVEs; replace with pycryptodome"),
    ];

    public ScannerDescriptor Descriptor { get; } = new(
        Id: "python.platform",
        Name: "Python Platform Scanner",
        Version: "0.1.0",
        Category: FindingCategory.Modernization,
        Capabilities: ["package-inventory", "interpreter-eol", "legacy-frameworks", "osv-pypi"]);

    public IReadOnlyList<RuleSpec> Rules { get; } =
    [
        new(RuleIds.Inventory, RulesVersion, FindingCategory.Quality, Severity.Informational,
            "Python footprint", "Python packages, interpreter targets and frameworks the estate depends on, from requirements/pyproject/Pipfile manifests.",
            null,
            Loc(new("Pegada Python", "Pacotes Python, interpretadores alvo e frameworks de que o sistema depende, a partir de requirements/pyproject/Pipfile.", null,
                "Pegada Python", "{packages} pacote(s) em {manifests} manifesto(s), {files} arquivo(s) .py; Python alvo: {targets}."))),
        new(RuleIds.InterpreterEol, RulesVersion, FindingCategory.Modernization, Severity.High,
            "Python interpreter target out of support", "The declared interpreter constraint admits a Python version that no longer receives security fixes (Python 2, or a 3.x past its end of life).",
            "Raise the floor to a supported Python (3.10+); Python 2 code needs the 2→3 migration first (six/futurize help inventory the surface).",
            Loc(new("Interpretador Python alvo fora de suporte", "A restrição declarada de interpretador admite uma versão de Python que não recebe mais correções de segurança (Python 2, ou um 3.x além do fim de vida).",
                "Suba o mínimo para um Python suportado (3.10+); código Python 2 exige antes a migração 2→3 (six/futurize ajudam a inventariar a superfície).",
                "Python {target} fora de suporte em {module}", "{module} declara {constraint}: {reason}"))),
        new(RuleIds.LegacyFramework, RulesVersion, FindingCategory.Modernization, Severity.Medium,
            "End-of-life Python framework", "A framework past its end of life (Django 1.x/2.x, Flask 1.x, Celery 4, nose, PyCrypto…) is a declared dependency: no security fixes and a blocker for interpreter upgrades.",
            "Inventory the code that depends on it and plan the upgrade path (Django LTS hops, pytest for nose, pycryptodome for PyCrypto).",
            Loc(new("Framework Python em fim de vida", "Um framework além do fim de vida (Django 1.x/2.x, Flask 1.x, Celery 4, nose, PyCrypto…) é dependência declarada: sem correções de segurança e bloqueio para upgrade do interpretador.",
                "Inventarie o código que depende dele e planeje o caminho de upgrade (saltos de LTS do Django, pytest no lugar do nose, pycryptodome no lugar do PyCrypto).",
                "Framework legado: {name}", "{name} declarado em {module} ({package} {version}): {reason}"))),
        new(RuleIds.VulnerablePackage, RulesVersion, FindingCategory.Dependencies, Severity.High,
            "Vulnerable PyPI dependency", "A pinned (==) PyPI dependency version matches a known vulnerability in the OSV database (offline bundle).",
            "Upgrade to the fixed version named in the finding; only pinned versions are judged — ranges are resolved at install time.",
            Loc(new("Dependência PyPI vulnerável", "Uma versão fixada (==) de dependência PyPI corresponde a uma vulnerabilidade conhecida na base OSV (bundle offline).",
                "Atualize para a versão corrigida indicada no finding; apenas versões fixadas são julgadas — ranges resolvem na instalação.",
                "{package} {version}: {vulnerability}", "{summary} Declarado em {module}. {fixedPt}"))),
    ];

    public async Task<ScanResult> ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var modules = new List<PythonModule>();
        foreach (var (pattern, parser) in new (string, Func<string, string, PythonModule>)[]
                 {
                     ("requirements.txt", ParseRequirements),
                     ("pyproject.toml", ParsePyProject),
                     ("Pipfile", ParsePipfile),
                     ("setup.py", ParseSetupPy),
                 })
        {
            foreach (var path in context.Workspace.SourceFiles(pattern).Where(p => !IsVendoredPath(p)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = await ReadAsync(context.Workspace, path, cancellationToken);
                if (text is not null)
                {
                    modules.Add(parser(path, text));
                }
            }
        }

        var pythonLanguage = context.Languages.GetValueOrDefault("python");
        if (modules.Count == 0 && pythonLanguage is null)
        {
            return ScanResult.Success();
        }

        EmitInventory(context, modules, pythonLanguage);
        EmitInterpreterEol(context, modules);
        EmitLegacyFrameworks(context, modules);
        await EmitVulnerabilitiesAsync(context, modules, cancellationToken);
        return ScanResult.Success();
    }

    private sealed record PythonDependency(string Package, string? Version, bool Pinned);

    private sealed record PythonModule(string Path, string Name, string? PythonRequires, IReadOnlyList<PythonDependency> Dependencies);

    private static void EmitInventory(ScanContext context, List<PythonModule> modules, Atlas.Language.Abstractions.LanguageAnalysisResult? language)
    {
        var targets = modules.Select(m => m.PythonRequires).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
        var packages = modules.SelectMany(m => m.Dependencies.Select(d => d.Package)).Distinct().Count();
        var files = language?.Totals.FileCount ?? 0;
        context.Findings.Emit(new FindingCandidate(
            RuleIds.Inventory, Severity.Informational, ConfidenceLevel.High,
            Title: "Python footprint",
            Message: $"{packages} package(s) across {modules.Count} manifest(s), {files} .py file(s); Python target(s): {(targets.Count == 0 ? "not declared" : string.Join(", ", targets))}.",
            Evidence: new EvidenceCandidate(FilePath: modules.FirstOrDefault()?.Path, Symbol: "python-platform"),
            Data: new Dictionary<string, string>
            {
                ["packages"] = packages.ToString(CultureInfo.InvariantCulture),
                ["manifests"] = modules.Count.ToString(CultureInfo.InvariantCulture),
                ["files"] = files.ToString(CultureInfo.InvariantCulture),
                ["targets"] = targets.Count == 0 ? "—" : string.Join(", ", targets),
            }));
    }

    private static void EmitInterpreterEol(ScanContext context, List<PythonModule> modules)
    {
        foreach (var module in modules)
        {
            if (string.IsNullOrWhiteSpace(module.PythonRequires))
            {
                continue;
            }

            var constraint = module.PythonRequires.Trim();
            string? target = null;
            string? reason = null;
            var severity = Severity.Medium;

            if (SupportsPython2Regex().IsMatch(constraint))
            {
                target = "2";
                severity = Severity.High;
                reason = "Python 2 reached end of life in January 2020; no fixes of any kind.";
            }
            else if (MinimumPython3Regex().Match(constraint) is { Success: true } m
                && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor)
                && minor <= 9)
            {
                target = $"3.{minor}";
                reason = $"Python 3.{minor} is past its end of life; the floor admits unsupported interpreters.";
            }

            if (target is null)
            {
                continue;
            }

            context.Findings.Emit(new FindingCandidate(
                RuleIds.InterpreterEol, severity, ConfidenceLevel.High,
                Title: $"Python {target} out of support in {module.Name}",
                Message: $"{module.Name} declares \"{constraint}\": {reason}",
                Evidence: new EvidenceCandidate(FilePath: module.Path, Symbol: $"python:{module.Name}"),
                Remediation: "Raise the floor to a supported Python (3.10+).",
                Data: new Dictionary<string, string> { ["target"] = target, ["module"] = module.Name, ["constraint"] = constraint, ["reason"] = reason! }));
        }
    }

    private void EmitLegacyFrameworks(ScanContext context, List<PythonModule> modules)
    {
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            foreach (var dependency in module.Dependencies)
            {
                foreach (var (candidate, name, belowMajor, severity, reason) in LegacyFrameworks)
                {
                    if (!dependency.Package.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (belowMajor is { } max && (MajorOf(dependency.Version) is not { } major || major >= max))
                    {
                        continue;
                    }

                    if (!emitted.Add(name))
                    {
                        break;
                    }

                    context.Findings.Emit(new FindingCandidate(
                        RuleIds.LegacyFramework, severity, ConfidenceLevel.High,
                        Title: $"Legacy framework: {name}",
                        Message: $"{name} is declared in {module.Name} ({dependency.Package} {dependency.Version ?? "unpinned"}): {reason}.",
                        Evidence: new EvidenceCandidate(FilePath: module.Path, Symbol: $"python:{name}"),
                        Remediation: Rules.First(r => r.Id == RuleIds.LegacyFramework).Remediation,
                        Data: new Dictionary<string, string>
                        {
                            ["name"] = name, ["module"] = module.Name, ["package"] = dependency.Package,
                            ["version"] = dependency.Version ?? "?", ["reason"] = reason,
                        }));
                    break; // first matching row wins for this package
                }
            }
        }
    }

    private async Task EmitVulnerabilitiesAsync(ScanContext context, List<PythonModule> modules, CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emitted = 0;
        foreach (var module in modules)
        {
            foreach (var dependency in module.Dependencies)
            {
                if (!dependency.Pinned
                    || dependency.Version is not { Length: > 0 } version
                    || !char.IsAsciiDigit(version[0])
                    || version.Contains('*'))
                {
                    continue; // only exact pins can be judged honestly
                }

                if (!seen.Add($"{dependency.Package}@{version}") || emitted >= MaxVulnerabilityFindings)
                {
                    continue;
                }

                IReadOnlyList<VulnerabilityMatch> matches;
                try
                {
                    matches = await vulnerabilities.FindAsync("PyPI", dependency.Package, version, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    continue; // bundle unreadable: dependency findings are best-effort
                }

                foreach (var match in matches)
                {
                    if (emitted++ >= MaxVulnerabilityFindings)
                    {
                        break;
                    }

                    context.Findings.Emit(new FindingCandidate(
                        RuleIds.VulnerablePackage, MapOsvSeverity(match.Severity), ConfidenceLevel.High,
                        Title: $"PyPI: {dependency.Package} {version}: {match.Id}",
                        Message: $"{match.Summary ?? $"Known vulnerability {match.Id}."} Declared in {module.Name}.",
                        Evidence: new EvidenceCandidate(FilePath: module.Path, Symbol: $"{dependency.Package}@{version}:{match.Id}"),
                        Remediation: match.FixedVersion is null
                            ? "No fixed version published; evaluate mitigation or replacement."
                            : $"Upgrade {dependency.Package} to {match.FixedVersion} or later.",
                        Data: new Dictionary<string, string>
                        {
                            ["package"] = dependency.Package, ["version"] = version, ["vulnerability"] = match.Id,
                            ["module"] = module.Name, ["summary"] = match.Summary ?? string.Empty,
                            ["fixedVersion"] = match.FixedVersion ?? string.Empty,
                            ["fixedPt"] = match.FixedVersion is null ? "Nenhuma versão corrigida publicada." : $"Corrigido a partir da versão {match.FixedVersion}.",
                            ["aliases"] = string.Join(",", match.Aliases),
                        }));
                }
            }
        }
    }

    private static PythonModule ParseRequirements(string path, string text)
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

            if (line.Length == 0 || line.StartsWith('-'))
            {
                continue; // options (-r, -e, --index-url…) are not packages
            }

            if (RequirementRegex().Match(line) is { Success: true } m)
            {
                var (version, pinned) = InterpretSpecifier(m.Groups[2].Success ? m.Groups[2].Value : null, m.Groups[3].Success ? m.Groups[3].Value : null);
                dependencies.Add(new PythonDependency(NormalizeName(m.Groups[1].Value), version, pinned));
            }
        }

        return new PythonModule(path, ModuleName(path), null, dependencies);
    }

    private static PythonModule ParsePyProject(string path, string text)
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

    private static PythonModule ParsePipfile(string path, string text)
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

    private static PythonModule ParseSetupPy(string path, string text)
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
    private static (string? Version, bool Pinned) InterpretSpecifier(string? op, string? version) =>
        version is null ? (null, false) : (version, op is "==" or "===");

    /// <summary>A Poetry/Pipfile value string: "1.2.3"/"==1.2.3" pin; "^1.11"/"&gt;=1.0"/"~1.2" floor; "*" nothing.</summary>
    private static (string? Version, bool Pinned) InterpretValue(string? value)
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

    private static string ModuleName(string path)
    {
        var directory = Path.GetFileName(Path.GetDirectoryName(path.Replace('\\', '/')));
        return string.IsNullOrEmpty(directory) ? "python-app" : directory!;
    }

    /// <summary>PyPI names are case-insensitive with '-'/'_' equivalent; OSV uses the lowercase dashed form.</summary>
    private static string NormalizeName(string package) => package.ToLowerInvariant().Replace('_', '-');

    private static int? MajorOf(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var dot = version.IndexOf('.');
        return int.TryParse(dot < 0 ? version : version[..dot], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) ? major : null;
    }

    private static Severity MapOsvSeverity(string? severity) =>
        severity?.Trim().ToUpperInvariant() switch
        {
            "CRITICAL" => Severity.Critical,
            "HIGH" => Severity.High,
            "MODERATE" or "MEDIUM" => Severity.Medium,
            "LOW" => Severity.Low,
            { } vector when vector.StartsWith("CVSS:", StringComparison.Ordinal) => CvssVector.ToSeverity(vector) ?? Severity.Medium,
            _ => Severity.Medium,
        };

    private static bool IsVendoredPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/site-packages/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/.tox/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/.eggs/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(".egg-info/", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> ReadAsync(IArtifactReader workspace, string path, CancellationToken cancellationToken)
    {
        try
        {
            return await workspace.ReadAllTextAsync(path, cancellationToken);
        }
        catch (IOException)
        {
            return null;
        }
    }

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

    [GeneratedRegex(@"(^|[^0-9.])2(\.(\d+|\*))*([^0-9.*]|$)", RegexOptions.None, 2000)]
    private static partial Regex SupportsPython2Regex();

    [GeneratedRegex(@"(?:>=?|~=|\^)\s*3\.(\d+)", RegexOptions.None, 2000)]
    private static partial Regex MinimumPython3Regex();
}
