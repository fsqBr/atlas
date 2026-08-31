using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Atlas.Domain.Findings;
using Atlas.Domain.Rules;
using Atlas.Domain.Workspaces;
using Atlas.Scanner.Abstractions;
using Atlas.Scanner.Dependencies.Vulnerabilities;

namespace Atlas.Scanner.Java;

/// <summary>
/// Java platform footprint (Tier 1.5 — the project system as data): Maven/Gradle modules,
/// JDK target and its support status, end-of-life frameworks, the javax→jakarta migration
/// surface, and known vulnerabilities from the offline OSV bundle when it carries the Maven
/// ecosystem (add the Maven export to Atlas:Vulnerabilities:SyncUrls to enable it). Manifests
/// only — no build, no dependency resolution; declared versions are what gets judged.
/// </summary>
public sealed partial class JavaScanner(IVulnerabilitySource vulnerabilities) : IScanner
{
    public static class RuleIds
    {
        public const string Inventory = "java.inventory";
        public const string JdkEol = "java.jdk.eol";
        public const string LegacyFramework = "java.legacy-framework";
        public const string JavaxNamespace = "java.javax-namespace";
        public const string VulnerablePackage = "java.vulnerable-dependency";
    }

    private const string RulesVersion = "1.0.0";
    private const string Pt = "pt-BR";
    private const int MaxVulnerabilityFindings = 100;

    private static IReadOnlyDictionary<string, RuleLocalization> Loc(RuleLocalization pt) =>
        new Dictionary<string, RuleLocalization> { [Pt] = pt };

    /// <summary>"group:artifact" prefix → display name; BelowMajor flags only versions with a smaller major (null = any version).</summary>
    private static readonly (string Prefix, string Name, int? BelowMajor, Severity Severity, string Reason)[] LegacyFrameworks =
    [
        ("log4j:log4j", "Log4j 1.x", null, Severity.High, "end of life since 2015 with unfixed CVEs (e.g. CVE-2019-17571)"),
        ("org.apache.struts:struts-core", "Struts 1", null, Severity.High, "end of life since 2013; no security fixes"),
        ("struts:struts", "Struts 1", null, Severity.High, "end of life since 2013; no security fixes"),
        ("org.apache.axis:axis", "Apache Axis 1", null, Severity.High, "end of life; known unfixed CVEs"),
        ("org.springframework:spring-core", "Spring Framework 4.x or older", 5, Severity.Medium, "out of open-source support; plan for Spring 6 / Boot 3"),
        ("org.springframework:spring-context", "Spring Framework 4.x or older", 5, Severity.Medium, "out of open-source support; plan for Spring 6 / Boot 3"),
        ("org.springframework.boot:spring-boot-starter-parent", "Spring Boot 1.x", 2, Severity.High, "end of life; no security fixes"),
        ("org.springframework.boot:spring-boot-starter", "Spring Boot 1.x", 2, Severity.High, "end of life; no security fixes"),
        ("org.springframework.boot:spring-boot-starter-web", "Spring Boot 1.x", 2, Severity.High, "end of life; no security fixes"),
        ("org.hibernate:hibernate-core", "Hibernate ORM 4.x or older", 5, Severity.Medium, "out of support"),
        ("commons-httpclient:commons-httpclient", "Commons HttpClient 3", null, Severity.Medium, "end of life; replaced by Apache HttpComponents"),
        ("javax.faces:jsf-api", "JSF 1.x (javax.faces)", null, Severity.Medium, "end of life; moved to Jakarta Faces"),
    ];

    public ScannerDescriptor Descriptor { get; } = new(
        Id: "java.platform",
        Name: "Java Platform Scanner",
        Version: "0.1.0",
        Category: FindingCategory.Modernization,
        Capabilities: ["module-inventory", "jdk-eol", "legacy-frameworks", "javax-migration", "osv-maven"]);

    public IReadOnlyList<RuleSpec> Rules { get; } =
    [
        new(RuleIds.Inventory, RulesVersion, FindingCategory.Quality, Severity.Informational,
            "Java footprint", "Java modules, JDK targets and frameworks the estate depends on, from Maven/Gradle manifests.",
            null,
            Loc(new("Pegada Java", "Módulos Java, JDKs alvo e frameworks de que o sistema depende, a partir de manifestos Maven/Gradle.", null,
                "Pegada Java", "{modules} módulo(s), {files} arquivo(s) .java; JDK(s): {jdks}."))),
        new(RuleIds.JdkEol, RulesVersion, FindingCategory.Modernization, Severity.High,
            "JDK target out of support", "The module targets a Java version that no longer receives public updates; running on it means unpatched JVM CVEs and blocks modern dependencies.",
            "Upgrade to a maintained LTS (Java 17 or 21); most 8→17 migrations are mechanical except for removed javax.* modules.",
            Loc(new("JDK alvo fora de suporte", "O módulo tem como alvo uma versão de Java que não recebe mais atualizações públicas; rodar nela significa CVEs de JVM sem correção e bloqueia dependências modernas.",
                "Atualize para um LTS mantido (Java 17 ou 21); a maioria das migrações 8→17 é mecânica, exceto módulos javax.* removidos.",
                "JDK {jdk} fora de suporte em {module}", "{module} tem como alvo Java {jdk}: {reason}"))),
        new(RuleIds.LegacyFramework, RulesVersion, FindingCategory.Modernization, Severity.Medium,
            "End-of-life Java framework", "A framework past its end of life (Struts 1, Log4j 1.x, Spring 4, Boot 1.x, Axis 1…) is a declared dependency: no security fixes and a hard blocker for JDK upgrades.",
            "Inventory the code that depends on it and plan the replacement (Struts→Spring MVC/Jakarta, Log4j 1→Log4j 2/SLF4J, Spring 4→6).",
            Loc(new("Framework Java em fim de vida", "Um framework além do fim de vida (Struts 1, Log4j 1.x, Spring 4, Boot 1.x, Axis 1…) é dependência declarada: sem correções de segurança e bloqueio duro para upgrade de JDK.",
                "Inventarie o código que depende dele e planeje a substituição (Struts→Spring MVC/Jakarta, Log4j 1→Log4j 2/SLF4J, Spring 4→6).",
                "Framework legado: {name}", "{name} declarado em {module} ({coordinates} {version}): {reason}"))),
        new(RuleIds.JavaxNamespace, RulesVersion, FindingCategory.Modernization, Severity.Medium,
            "javax.* dependencies (Jakarta migration pending)", "Dependencies still in the javax.* namespace pin the estate to Java EE 8-era APIs; Spring Boot 3+, Jakarta EE 9+ and modern app servers require the jakarta.* namespace.",
            "Plan the javax→jakarta rename as part of the JDK/framework upgrade; tooling (OpenRewrite, Eclipse Transformer) automates most of it.",
            Loc(new("Dependências javax.* (migração Jakarta pendente)", "Dependências ainda no namespace javax.* prendem o sistema às APIs da era Java EE 8; Spring Boot 3+, Jakarta EE 9+ e servidores modernos exigem o namespace jakarta.*.",
                "Planeje a renomeação javax→jakarta junto do upgrade de JDK/framework; ferramentas (OpenRewrite, Eclipse Transformer) automatizam a maior parte.",
                "{count} dependência(s) javax.* em {module}", "Namespace javax.* declarado: {sample}"))),
        new(RuleIds.VulnerablePackage, RulesVersion, FindingCategory.Dependencies, Severity.High,
            "Vulnerable Maven dependency", "A declared Maven dependency version matches a known vulnerability in the OSV database (offline bundle).",
            "Upgrade to the fixed version named in the finding; declared versions are what was judged — a dependency-management override may already fix it.",
            Loc(new("Dependência Maven vulnerável", "Uma versão declarada de dependência Maven corresponde a uma vulnerabilidade conhecida na base OSV (bundle offline).",
                "Atualize para a versão corrigida indicada no finding; o que foi julgado é a versão declarada — um override de dependency-management pode já corrigir.",
                "{package} {version}: {vulnerability}", "{summary} Declarado em {module}. {fixedPt}"))),
    ];

    public async Task<ScanResult> ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var modules = new List<JavaModule>();
        foreach (var path in context.Workspace.SourceFiles("pom.xml").Where(p => !IsBuildOutputPath(p)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await ReadAsync(context.Workspace, path, cancellationToken);
            if (text is not null && ParsePom(path, text) is { } module)
            {
                modules.Add(module);
            }
        }

        foreach (var path in context.Workspace.SourceFiles("build.gradle").Concat(context.Workspace.SourceFiles("build.gradle.kts")).Where(p => !IsBuildOutputPath(p)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await ReadAsync(context.Workspace, path, cancellationToken);
            if (text is not null)
            {
                modules.Add(ParseGradle(path, text));
            }
        }

        var javaLanguage = context.Languages.GetValueOrDefault("java");
        if (modules.Count == 0 && javaLanguage is null)
        {
            return ScanResult.Success();
        }

        EmitInventory(context, modules, javaLanguage);
        EmitJdkEol(context, modules);
        EmitLegacyFrameworks(context, modules);
        EmitJavaxNamespace(context, modules);
        await EmitVulnerabilitiesAsync(context, modules, cancellationToken);
        return ScanResult.Success();
    }

    private sealed record JavaModule(string Path, string Name, string? Jdk, IReadOnlyList<(string Group, string Artifact, string? Version)> Dependencies);

    private static void EmitInventory(ScanContext context, List<JavaModule> modules, Atlas.Language.Abstractions.LanguageAnalysisResult? language)
    {
        var jdks = modules.Select(m => NormalizeJdk(m.Jdk)).Where(v => v is not null).Select(v => v!.Value).Distinct().OrderBy(v => v).ToList();
        var files = language?.Totals.FileCount ?? 0;
        context.Findings.Emit(new FindingCandidate(
            RuleIds.Inventory, Severity.Informational, ConfidenceLevel.High,
            Title: "Java footprint",
            Message: $"{modules.Count} module(s), {files} .java file(s); JDK target(s): {(jdks.Count == 0 ? "not declared" : string.Join(", ", jdks))}.",
            Evidence: new EvidenceCandidate(FilePath: modules.FirstOrDefault()?.Path, Symbol: "java-platform"),
            Data: new Dictionary<string, string>
            {
                ["modules"] = modules.Count.ToString(CultureInfo.InvariantCulture),
                ["files"] = files.ToString(CultureInfo.InvariantCulture),
                ["jdks"] = jdks.Count == 0 ? "—" : string.Join(", ", jdks),
                ["moduleNames"] = string.Join(", ", modules.Select(m => m.Name).Distinct().Take(20)),
            }));
    }

    private static void EmitJdkEol(ScanContext context, List<JavaModule> modules)
    {
        foreach (var module in modules)
        {
            if (NormalizeJdk(module.Jdk) is not { } jdk || jdk >= 17)
            {
                continue;
            }

            var (severity, reason) = jdk <= 8
                ? (Severity.High, $"public updates for Java {jdk} ended; only paid extended support remains.")
                : jdk == 11
                    ? (Severity.Medium, "Java 11 is an aging LTS at the end of its free support window.")
                    : (Severity.Medium, $"Java {jdk} is a non-LTS release that stopped receiving updates six months after GA.");

            context.Findings.Emit(new FindingCandidate(
                RuleIds.JdkEol, severity, ConfidenceLevel.High,
                Title: $"JDK {jdk} out of support in {module.Name}",
                Message: $"{module.Name} targets Java {jdk}: {reason}",
                Evidence: new EvidenceCandidate(FilePath: module.Path, Symbol: $"jdk:{module.Name}"),
                Remediation: "Upgrade to a maintained LTS (Java 17 or 21).",
                Data: new Dictionary<string, string> { ["jdk"] = jdk.ToString(CultureInfo.InvariantCulture), ["module"] = module.Name, ["reason"] = reason }));
        }
    }

    private void EmitLegacyFrameworks(ScanContext context, List<JavaModule> modules)
    {
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            foreach (var (group, artifact, version) in module.Dependencies)
            {
                var coordinates = $"{group}:{artifact}";
                foreach (var (prefix, name, belowMajor, severity, reason) in LegacyFrameworks)
                {
                    if (!coordinates.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                        || (belowMajor is { } max && (MajorOf(version) is not { } major || major >= max))
                        || !emitted.Add(name))
                    {
                        continue;
                    }

                    context.Findings.Emit(new FindingCandidate(
                        RuleIds.LegacyFramework, severity, ConfidenceLevel.High,
                        Title: $"Legacy framework: {name}",
                        Message: $"{name} is declared in {module.Name} ({coordinates} {version ?? "version unresolved"}): {reason}.",
                        Evidence: new EvidenceCandidate(FilePath: module.Path, Symbol: $"java:{name}"),
                        Remediation: Rules.First(r => r.Id == RuleIds.LegacyFramework).Remediation,
                        Data: new Dictionary<string, string>
                        {
                            ["name"] = name, ["module"] = module.Name, ["coordinates"] = coordinates,
                            ["version"] = version ?? "?", ["reason"] = reason,
                        }));
                }
            }
        }
    }

    private void EmitJavaxNamespace(ScanContext context, List<JavaModule> modules)
    {
        foreach (var module in modules)
        {
            var javax = module.Dependencies.Where(d => d.Group.StartsWith("javax.", StringComparison.OrdinalIgnoreCase) || d.Group.Equals("javax", StringComparison.OrdinalIgnoreCase)).ToList();
            if (javax.Count == 0)
            {
                continue;
            }

            var sample = string.Join(", ", javax.Take(5).Select(d => $"{d.Group}:{d.Artifact}"));
            context.Findings.Emit(new FindingCandidate(
                RuleIds.JavaxNamespace, Severity.Medium, ConfidenceLevel.High,
                Title: $"{javax.Count} javax.* dependency(ies) in {module.Name}",
                Message: $"javax.* namespace declared: {sample}{(javax.Count > 5 ? ", …" : string.Empty)}. Spring Boot 3+/Jakarta EE 9+ require jakarta.*.",
                Evidence: new EvidenceCandidate(FilePath: module.Path, Symbol: $"javax:{module.Name}"),
                Remediation: Rules.First(r => r.Id == RuleIds.JavaxNamespace).Remediation,
                Data: new Dictionary<string, string> { ["count"] = javax.Count.ToString(CultureInfo.InvariantCulture), ["module"] = module.Name, ["sample"] = sample }));
        }
    }

    private async Task EmitVulnerabilitiesAsync(ScanContext context, List<JavaModule> modules, CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emitted = 0;
        foreach (var module in modules)
        {
            foreach (var (group, artifact, version) in module.Dependencies)
            {
                if (version is null || version.Length == 0 || !char.IsAsciiDigit(version[0]) || version.Contains('$'))
                {
                    continue; // unresolved or range-managed versions cannot be judged honestly
                }

                var packageId = $"{group}:{artifact}";
                if (!seen.Add($"{packageId}@{version}") || emitted >= MaxVulnerabilityFindings)
                {
                    continue;
                }

                IReadOnlyList<VulnerabilityMatch> matches;
                try
                {
                    matches = await vulnerabilities.FindAsync("Maven", packageId, version, cancellationToken);
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
                        Title: $"Maven: {packageId} {version}: {match.Id}",
                        Message: $"{match.Summary ?? $"Known vulnerability {match.Id}."} Declared in {module.Name}.",
                        Evidence: new EvidenceCandidate(FilePath: module.Path, Symbol: $"{packageId}@{version}:{match.Id}"),
                        Remediation: match.FixedVersion is null
                            ? "No fixed version published; evaluate mitigation or replacement."
                            : $"Upgrade {packageId} to {match.FixedVersion} or later.",
                        Data: new Dictionary<string, string>
                        {
                            ["package"] = packageId, ["version"] = version, ["vulnerability"] = match.Id,
                            ["module"] = module.Name, ["summary"] = match.Summary ?? string.Empty,
                            ["fixedVersion"] = match.FixedVersion ?? string.Empty,
                            ["fixedPt"] = match.FixedVersion is null ? "Nenhuma versão corrigida publicada." : $"Corrigido a partir da versão {match.FixedVersion}.",
                            ["aliases"] = string.Join(",", match.Aliases),
                        }));
                }
            }
        }
    }

    private static JavaModule? ParsePom(string path, string xml)
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
            name = Path.GetFileName(Path.GetDirectoryName(path.Replace('\\', '/')));
        }

        return new JavaModule(path, string.IsNullOrEmpty(name) ? "maven-module" : name!, jdk, dependencies);
    }

    private static JavaModule ParseGradle(string path, string text)
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

        var directory = Path.GetFileName(Path.GetDirectoryName(path.Replace('\\', '/')));
        return new JavaModule(path, string.IsNullOrEmpty(directory) ? "gradle-module" : directory!, jdk, dependencies);
    }

    /// <summary>Maven copies the module pom under target/ on every build: judging it double-counts modules.</summary>
    private static bool IsBuildOutputPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/target/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("target/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/build/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/.gradle/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"1.8" → 8, "8" → 8, "17" → 17; anything else null.</summary>
    private static int? NormalizeJdk(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var v = value.Trim();
        if (v.StartsWith("1.", StringComparison.Ordinal))
        {
            v = v[2..];
        }

        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed is > 0 and < 100 ? parsed : null;
    }

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
