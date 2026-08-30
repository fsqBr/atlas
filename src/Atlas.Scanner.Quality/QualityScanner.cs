using System.Globalization;
using System.Text.RegularExpressions;
using Atlas.Domain.Findings;
using Atlas.Domain.Rules;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;

namespace Atlas.Scanner.Quality;

/// <summary>
/// Quality scanner (.7/§20.8): test discovery and test-to-production
/// coverage by reference, ingested coverage reports, complexity hotspots, large
/// and unparseable files. "Test existence != meaningful coverage" — both are
/// reported separately and honestly (no data ≠ zero). Bilingual rules.
/// </summary>
public sealed partial class QualityScanner : IScanner
{
    public static class RuleIds
    {
        public const string NoTests = "quality.tests.none";
        public const string ProjectUncovered = "quality.tests.project-uncovered";
        public const string CoverageNoData = "quality.coverage.no-data";
        public const string CoverageSummary = "quality.coverage.summary";
        public const string CoverageLow = "quality.coverage.low";
        public const string ComplexMethod = "quality.complexity.method";
        public const string LargeFile = "quality.file.large";
        public const string SyntaxError = "quality.file.syntax-error";
        public const string Duplication = "quality.duplication.block";
        public const string LegacyApi = "quality.api.legacy";
        public const string ObsoleteApi = "quality.api.obsolete";
    }

    private const int MaxDuplicationFindings = 200;
    private const int MaxApiFindings = 500;

    private const string RulesVersion = "1.0.0";
    private const string Pt = "pt-BR";
    private const int ComplexityMedium = 15;
    private const int ComplexityHigh = 30;
    private const int LargeFileLines = 1000;
    private const double LowCoverage = 0.5;

    private static readonly string[] TestPackageMarkers = ["xunit", "nunit", "mstest", "microsoft.net.test.sdk"];
    private static readonly string[] TestAssemblyMarkers = ["nunit.framework", "microsoft.visualstudio.qualitytools", "xunit"];

    [GeneratedRegex(@"(\.|^)(Unit|Integration)?Tests?$", RegexOptions.IgnoreCase)]
    private static partial Regex TestProjectName();

    public ScannerDescriptor Descriptor { get; } = new(
        Id: "quality.core",
        Name: "Quality Scanner",
        Version: "0.2.0",
        Category: FindingCategory.Quality,
        Capabilities: ["test-discovery", "coverage-ingestion", "complexity", "file-size", "duplication", "legacy-apis"]);

    private static IReadOnlyDictionary<string, RuleLocalization> Loc(RuleLocalization pt) =>
        new Dictionary<string, RuleLocalization> { [Pt] = pt };

    public IReadOnlyList<RuleSpec> Rules { get; } =
    [
        new(RuleIds.NoTests, RulesVersion, FindingCategory.Quality, Severity.High,
            "No automated tests found", "No test project or test method was detected in the estate.",
            "Start with characterization tests around the most central components before any modernization.",
            Loc(new("Nenhum teste automatizado encontrado", "Nenhum projeto de teste ou método de teste foi detectado no estate.",
                "Comece com testes de caracterização em torno dos componentes mais centrais antes de qualquer modernização.",
                "Nenhum teste automatizado encontrado", "{projectCount} projeto(s), nenhum projeto de teste e nenhum método de teste detectado."))),
        new(RuleIds.ProjectUncovered, RulesVersion, FindingCategory.Quality, Severity.Medium,
            "Production project without tests", "No test project references this production project.",
            "Add a test project referencing it; prioritize by centrality and business criticality.",
            Loc(new("Projeto de produção sem testes", "Nenhum projeto de teste referencia este projeto de produção.",
                "Adicione um projeto de teste que o referencie; priorize por centralidade e criticidade de negócio.",
                "{projectName} não tem projeto de teste", "Nenhum dos {testProjectCount} projeto(s) de teste referencia {projectName}."))),
        new(RuleIds.CoverageNoData, RulesVersion, FindingCategory.Quality, Severity.Informational,
            "No coverage data available", "No Cobertura/coverlet coverage report was found in the repository; coverage is unknown, not zero.",
            "Publish coverage reports (coverlet, Cobertura format) into the repository or CI artifacts Atlas can read.",
            Loc(new("Sem dados de cobertura", "Nenhum relatório de cobertura Cobertura/coverlet foi encontrado no repositório; a cobertura é desconhecida, não zero.",
                "Publique relatórios de cobertura (coverlet, formato Cobertura) no repositório ou em artefatos de CI que o Atlas possa ler.",
                "Sem dados de cobertura", "Nenhum relatório Cobertura/coverlet encontrado; a cobertura é desconhecida (não zero)."))),
        new(RuleIds.CoverageSummary, RulesVersion, FindingCategory.Quality, Severity.Informational,
            "Coverage report summary", "Overall line coverage from an ingested report.", null,
            Loc(new("Resumo do relatório de cobertura", "Cobertura de linhas geral de um relatório ingerido.", null,
                "Cobertura de linhas {lineRatePct}% ({fileName})", "{packageCount} pacote(s) no relatório; cobertura de linhas geral {lineRatePct}%."))),
        new(RuleIds.CoverageLow, RulesVersion, FindingCategory.Quality, Severity.Medium,
            "Low line coverage", "A package/assembly is below 50% line coverage in the ingested report.",
            "Raise coverage on central and business-critical packages first.",
            Loc(new("Cobertura de linhas baixa", "Um pacote/assembly está abaixo de 50% de cobertura de linhas no relatório ingerido.",
                "Aumente a cobertura primeiro nos pacotes centrais e críticos para o negócio.",
                "{package}: {lineRatePct}% de cobertura de linhas", "Abaixo do limiar de 50% em {fileName}."))),
        new(RuleIds.ComplexMethod, RulesVersion, FindingCategory.Quality, Severity.Medium,
            "High cyclomatic complexity", "Method complexity makes the code hard to test and risky to change.",
            "Extract methods, replace conditionals with polymorphism/strategy, add characterization tests first.",
            Loc(new("Complexidade ciclomática alta", "A complexidade do método torna o código difícil de testar e arriscado de mudar.",
                "Extraia métodos, substitua condicionais por polimorfismo/strategy, adicione testes de caracterização primeiro.",
                "{symbol}: complexidade {complexity}", "Complexidade ciclomática {complexity} em {lines} linhas."))),
        new(RuleIds.LargeFile, RulesVersion, FindingCategory.Quality, Severity.Low,
            "Very large source file", "Files above 1,000 lines usually hold several responsibilities.",
            "Split by responsibility; large files correlate with merge conflicts and defects.",
            Loc(new("Arquivo-fonte muito grande", "Arquivos acima de 1.000 linhas geralmente acumulam várias responsabilidades.",
                "Divida por responsabilidade; arquivos grandes correlacionam com conflitos de merge e defeitos.",
                "{fileName}: {lines} linhas", "{lines} linhas, {types} tipo(s), {methods} método(s)."))),
        new(RuleIds.SyntaxError, RulesVersion, FindingCategory.Quality, Severity.Low,
            "File with syntax errors", "The file could not be fully parsed; analysis of it is partial.",
            "Fix or exclude the file; check for generated or partial code committed by mistake.",
            Loc(new("Arquivo com erros de sintaxe", "O arquivo não pôde ser totalmente analisado; a análise dele é parcial.",
                "Corrija ou exclua o arquivo; verifique código gerado ou parcial commitado por engano.",
                "{fileName} tem erros de sintaxe", "O parser reportou erros; os fatos deste arquivo são parciais."))),
        new(RuleIds.Duplication, RulesVersion, FindingCategory.Quality, Severity.Medium,
            "Duplicated code block", "The same normalized block of code appears in more than one place: fixes must be repeated and drift is likely.",
            "Extract the block into one method/class and reuse it; cover it with a test first.",
            Loc(new("Bloco de código duplicado", "O mesmo bloco de código (normalizado) aparece em mais de um lugar: correções precisam ser repetidas e a divergência é provável.",
                "Extraia o bloco para um método/classe e reutilize; cubra com teste antes.",
                "Bloco duplicado em {fileName} ({lines} linhas)", "{lines} linhas duplicadas a partir da linha {line}; também em {other}."))),
        new(RuleIds.LegacyApi, RulesVersion, FindingCategory.Quality, Severity.Medium,
            "Legacy API not available or discouraged on modern .NET", "The code calls an API that is gone, throws, or is discouraged on modern .NET; it fails at compile or run time after migration.",
            "Replace with the modern equivalent named in the finding (HttpClient, CancellationToken, AssemblyLoadContext, IConfiguration, IHttpContextAccessor…).",
            Loc(new("API legada indisponível ou desencorajada no .NET moderno", "O código usa uma API que não existe, lança exceção ou é desencorajada no .NET moderno; falha em compilação ou execução após a migração.",
                "Substitua pelo equivalente moderno indicado no finding (HttpClient, CancellationToken, AssemblyLoadContext, IConfiguration, IHttpContextAccessor…).",
                "API legada {api} em {fileName} (×{count})", "{detail} — {count} uso(s) nas linhas {lines}."))),
        new(RuleIds.ObsoleteApi, RulesVersion, FindingCategory.Quality, Severity.Low,
            "Obsolete API usage ([Obsolete])", "The code uses a member marked [Obsolete]; it may be removed in a future version and usually has a documented replacement.",
            "Follow the attribute's message and move to the replacement.",
            Loc(new("Uso de API obsoleta ([Obsolete])", "O código usa um membro marcado [Obsolete]; pode ser removido em versão futura e normalmente tem substituto documentado.",
                "Siga a mensagem do atributo e migre para o substituto.",
                "API obsoleta {api} em {fileName} (×{count})", "{detail} — {count} uso(s) nas linhas {lines}."))),
    ];

    public async Task<ScanResult> ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var languages = context.Languages.Values.ToList();
        var projects = languages.SelectMany(l => l.Projects).ToList();
        var files = languages.SelectMany(l => l.Files).ToList();

        EmitTestFacts(context, projects, files);
        EmitComplexity(context, languages.SelectMany(l => l.HotMethods));
        EmitFileFacts(context, files);
        EmitApiPatterns(context, languages.SelectMany(l => l.Patterns));
        await EmitDuplicationAsync(context, files, cancellationToken);
        await EmitCoverageAsync(context, cancellationToken);

        return ScanResult.Success();
    }

    /// <summary>One finding per (file, API): "HttpContext.Current ×12 in Default.aspx.cs", not twelve findings.</summary>
    private static void EmitApiPatterns(ScanContext context, IEnumerable<PatternFact> patterns)
    {
        var groups = patterns
            .Where(p => p.PatternId is QualityPatternIds.LegacyApi or QualityPatternIds.ObsoleteApi)
            .GroupBy(p => (p.PatternId, p.FilePath, Api: p.Detail.Split(':')[0].Trim()))
            .OrderBy(g => g.Key.FilePath, StringComparer.Ordinal).ThenBy(g => g.Key.Api, StringComparer.Ordinal)
            .Take(MaxApiFindings);

        foreach (var group in groups)
        {
            var (patternId, filePath, api) = group.Key;
            var ruleId = patternId == QualityPatternIds.LegacyApi ? RuleIds.LegacyApi : RuleIds.ObsoleteApi;
            var first = group.OrderBy(p => p.Line).First();
            var lines = group.Select(p => p.Line).Distinct().OrderBy(l => l).ToList();
            var fileName = Path.GetFileName(filePath);
            var kind = ruleId == RuleIds.LegacyApi ? "Legacy" : "Obsolete";

            context.Findings.Emit(new FindingCandidate(
                ruleId, ruleId == RuleIds.LegacyApi ? Severity.Medium : Severity.Low, ConfidenceLevel.High,
                Title: lines.Count == 1 ? $"{kind} API {api} in {fileName}" : $"{kind} API {api} in {fileName} (×{lines.Count})",
                Message: $"{first.Detail} — {lines.Count} usage(s) at line(s) {string.Join(", ", lines.Take(10))}{(lines.Count > 10 ? ", …" : string.Empty)}.",
                Evidence: new EvidenceCandidate(FilePath: filePath, LineStart: first.Line, Symbol: $"{fileName}:{api}"),
                Remediation: ruleId == RuleIds.LegacyApi
                    ? "Replace with the modern equivalent named in the finding."
                    : "Follow the attribute's message and move to the replacement.",
                Data: new Dictionary<string, string>
                {
                    ["detail"] = first.Detail, ["api"] = api, ["count"] = lines.Count.ToString(CultureInfo.InvariantCulture),
                    ["lines"] = string.Join(", ", lines.Take(10)), ["fileName"] = fileName,
                }));
        }
    }

    /// <summary>Copy-paste blocks across the analyzed source files (generated code excluded).</summary>
    private static async Task EmitDuplicationAsync(ScanContext context, IReadOnlyList<FileFact> files, CancellationToken cancellationToken)
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var path = file.RelativePath;
            if (path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) || path.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase)
                || path.Contains("\\Migrations\\", StringComparison.OrdinalIgnoreCase) || file.Lines < DuplicationDetector.MinBlockLines)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                sources[path] = await context.Workspace.ReadAllTextAsync(path, cancellationToken);
            }
            catch (IOException)
            {
            }
        }

        var blocks = DuplicationDetector.Detect(sources);
        foreach (var block in blocks.Take(MaxDuplicationFindings))
        {
            context.Findings.Emit(new FindingCandidate(
                RuleIds.Duplication, Severity.Medium, ConfidenceLevel.High,
                Title: $"Duplicated block in {Path.GetFileName(block.FilePath)} ({block.Lines} lines)",
                Message: $"{block.Lines} duplicated lines starting at line {block.StartLine}; also at {string.Join(", ", block.OtherLocations)}.",
                Evidence: new EvidenceCandidate(FilePath: block.FilePath, LineStart: block.StartLine, LineEnd: block.StartLine + block.Lines - 1, Symbol: block.Hash),
                Remediation: "Extract the block into one method/class and reuse it; cover it with a test first.",
                Data: new Dictionary<string, string>
                {
                    ["fileName"] = Path.GetFileName(block.FilePath),
                    ["lines"] = block.Lines.ToString(CultureInfo.InvariantCulture),
                    ["other"] = string.Join(", ", block.OtherLocations),
                }));
        }
    }

    private static void EmitTestFacts(ScanContext context, IReadOnlyList<ProjectFact> projects, IReadOnlyList<FileFact> files)
    {
        if (projects.Count == 0)
        {
            return;
        }

        var testProjects = projects.Where(IsTestProject).ToList();
        var totalTestMethods = files.Sum(f => f.TestMethodCount);

        if (testProjects.Count == 0 && totalTestMethods == 0)
        {
            context.Findings.Emit(new FindingCandidate(
                RuleIds.NoTests, Severity.High, ConfidenceLevel.High,
                Title: "No automated tests found",
                Message: $"{projects.Count} project(s), no test project and no test methods detected.",
                Evidence: new EvidenceCandidate(Symbol: "estate"),
                Data: new Dictionary<string, string> { ["projectCount"] = projects.Count.ToString(CultureInfo.InvariantCulture) }));
            return;
        }

        var byPath = projects.ToDictionary(p => Normalize(p.RelativePath), p => p, StringComparer.OrdinalIgnoreCase);
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var test in testProjects)
        {
            var dir = Path.GetDirectoryName(test.RelativePath) ?? string.Empty;
            foreach (var reference in test.ProjectReferences)
            {
                var resolved = Normalize(Path.GetRelativePath(".", Path.Combine(dir, reference.Replace('\\', Path.DirectorySeparatorChar))));
                if (byPath.TryGetValue(resolved, out var target))
                {
                    covered.Add(target.RelativePath);
                }
            }
        }

        foreach (var project in projects.Where(p => !IsTestProject(p) && !covered.Contains(p.RelativePath)))
        {
            context.Findings.Emit(new FindingCandidate(
                RuleIds.ProjectUncovered, Severity.Medium, ConfidenceLevel.High,
                Title: $"{project.Name} has no test project",
                Message: $"None of the {testProjects.Count} test project(s) references {project.Name}.",
                Evidence: new EvidenceCandidate(FilePath: project.RelativePath, Symbol: "no-test-reference"),
                Data: new Dictionary<string, string>
                {
                    ["projectName"] = project.Name,
                    ["testProjectCount"] = testProjects.Count.ToString(CultureInfo.InvariantCulture),
                }));
        }
    }

    private static void EmitComplexity(ScanContext context, IEnumerable<MethodFact> hotMethods)
    {
        foreach (var method in hotMethods.Where(m => m.CyclomaticComplexity >= ComplexityMedium))
        {
            context.Findings.Emit(new FindingCandidate(
                RuleIds.ComplexMethod,
                method.CyclomaticComplexity >= ComplexityHigh ? Severity.High : Severity.Medium,
                ConfidenceLevel.High,
                Title: $"{method.Symbol}: complexity {method.CyclomaticComplexity}",
                Message: $"Cyclomatic complexity {method.CyclomaticComplexity} over {method.Lines} lines.",
                Evidence: new EvidenceCandidate(FilePath: method.FilePath, LineStart: method.Line, Symbol: method.Symbol),
                Data: new Dictionary<string, string>
                {
                    ["complexity"] = method.CyclomaticComplexity.ToString(CultureInfo.InvariantCulture),
                    ["lines"] = method.Lines.ToString(CultureInfo.InvariantCulture),
                }));
        }
    }

    private static void EmitFileFacts(ScanContext context, IReadOnlyList<FileFact> files)
    {
        foreach (var file in files)
        {
            if (file.Lines > LargeFileLines)
            {
                context.Findings.Emit(new FindingCandidate(
                    RuleIds.LargeFile, Severity.Low, ConfidenceLevel.High,
                    Title: Inv($"{Path.GetFileName(file.RelativePath)}: {file.Lines:N0} lines"),
                    Message: Inv($"{file.Lines:N0} lines, {file.TypeCount} type(s), {file.MethodCount} method(s)."),
                    Evidence: new EvidenceCandidate(FilePath: file.RelativePath, Symbol: "large-file"),
                    Data: new Dictionary<string, string>
                    {
                        ["lines"] = file.Lines.ToString(CultureInfo.InvariantCulture),
                        ["types"] = file.TypeCount.ToString(CultureInfo.InvariantCulture),
                        ["methods"] = file.MethodCount.ToString(CultureInfo.InvariantCulture),
                    }));
            }

            if (file.HasSyntaxErrors)
            {
                context.Findings.Emit(new FindingCandidate(
                    RuleIds.SyntaxError, Severity.Low, ConfidenceLevel.High,
                    Title: $"{Path.GetFileName(file.RelativePath)} has syntax errors",
                    Message: "The parser reported errors; facts for this file are partial.",
                    Evidence: new EvidenceCandidate(FilePath: file.RelativePath, Symbol: "syntax-error")));
            }
        }
    }

    private static async Task EmitCoverageAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var reports = await CoberturaParser.FindAndParseAsync(context.Workspace, cancellationToken);
        if (reports.Count == 0)
        {
            context.Findings.Emit(new FindingCandidate(
                RuleIds.CoverageNoData, Severity.Informational, ConfidenceLevel.High,
                Title: "No coverage data available",
                Message: "No Cobertura/coverlet report found; coverage is unknown (not zero).",
                Evidence: new EvidenceCandidate(Symbol: "estate")));
            return;
        }

        foreach (var report in reports)
        {
            var pct = (report.LineRate * 100).ToString("F1", CultureInfo.InvariantCulture);
            context.Findings.Emit(new FindingCandidate(
                RuleIds.CoverageSummary, Severity.Informational, ConfidenceLevel.High,
                Title: Inv($"Line coverage {report.LineRate * 100:F1}% ({Path.GetFileName(report.Path)})"),
                Message: Inv($"{report.Packages.Count} package(s) in report; overall line rate {report.LineRate * 100:F1}%."),
                Evidence: new EvidenceCandidate(FilePath: report.Path, Symbol: "overall"),
                Data: new Dictionary<string, string>
                {
                    ["lineRate"] = report.LineRate.ToString("F4", CultureInfo.InvariantCulture),
                    ["lineRatePct"] = pct,
                    ["packageCount"] = report.Packages.Count.ToString(CultureInfo.InvariantCulture),
                }));

            foreach (var package in report.Packages.Where(p => p.LineRate < LowCoverage))
            {
                context.Findings.Emit(new FindingCandidate(
                    RuleIds.CoverageLow, Severity.Medium, ConfidenceLevel.High,
                    Title: Inv($"{package.Name}: {package.LineRate * 100:F1}% line coverage"),
                    Message: Inv($"Below the {LowCoverage * 100:F0}% threshold in {Path.GetFileName(report.Path)}."),
                    Evidence: new EvidenceCandidate(FilePath: report.Path, Symbol: package.Name),
                    Data: new Dictionary<string, string>
                    {
                        ["package"] = package.Name,
                        ["lineRate"] = package.LineRate.ToString("F4", CultureInfo.InvariantCulture),
                        ["lineRatePct"] = (package.LineRate * 100).ToString("F1", CultureInfo.InvariantCulture),
                    }));
            }
        }
    }

    public static bool IsTestProject(ProjectFact project) =>
        TestProjectName().IsMatch(project.Name)
        || project.PackageReferences.Any(p => TestPackageMarkers.Any(m => p.Id.Contains(m, StringComparison.OrdinalIgnoreCase)))
        || project.AssemblyReferences.Any(a => TestAssemblyMarkers.Any(m => a.StartsWith(m, StringComparison.OrdinalIgnoreCase)));

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('.', '/');

    /// <summary>Findings are data shared across cultures: numbers always render invariant.</summary>
    private static string Inv(FormattableString text) => FormattableString.Invariant(text);
}
