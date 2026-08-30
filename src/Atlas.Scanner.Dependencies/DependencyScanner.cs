using System.Globalization;
using Atlas.Domain.Findings;
using Atlas.Domain.Rules;
using Atlas.Domain.Workspaces;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;
using Atlas.Scanner.Dependencies.Vulnerabilities;

namespace Atlas.Scanner.Dependencies;

/// <summary>
/// First real Atlas scanner: turns DependencyAnalyzer output into finding
/// candidates. Symbols are chosen so identity survives line moves and file
/// reshuffles: project path + moniker, rule id, package@version:vuln.
/// Every candidate carries structured data so titles/messages can be rendered
/// in the reader's language (rules are bilingual, EN canonical + PT-BR).
/// </summary>
public sealed class DependencyScanner(IVulnerabilitySource vulnerabilities) : IScanner
{
    public static class RuleIds
    {
        public const string FrameworkEndOfLife = "dependency.framework.end-of-life";
        public const string FrameworkEndingSoon = "dependency.framework.ending-soon";
        public const string FrameworkLegacy = "dependency.framework.legacy";
        public const string FrameworkUnknown = "dependency.framework.unknown";
        public const string MigrationBlockerPrefix = "dependency.migration-blocker.";
        public const string VulnerablePackage = "dependency.package.vulnerable";
        public const string NpmInventory = "dependency.npm.inventory";
        public const string VersionConflict = "dependency.package.version-conflict";
        public const string UnresolvedProjectReference = "dependency.project-reference.unresolved";

        public static string MigrationBlocker(string blockerRuleId) => MigrationBlockerPrefix + blockerRuleId.ToLowerInvariant();
    }

    private const string RulesVersion = "1.0.0";
    private const string Pt = "pt-BR";

    public ScannerDescriptor Descriptor { get; } = new(
        Id: "dependency.nuget",
        Name: "Dependency & Framework Scanner",
        Version: "0.2.0",
        Category: FindingCategory.Dependencies,
        Capabilities: ["frameworks", "migration-blockers", "vulnerabilities", "version-conflicts", "project-graph"]);

    public IReadOnlyList<RuleSpec> Rules { get; } = BuildRules();

    private static IReadOnlyList<RuleSpec> BuildRules()
    {
        var rules = new List<RuleSpec>
        {
            new(RuleIds.FrameworkEndOfLife, RulesVersion, FindingCategory.Modernization, Severity.High,
                "Target framework out of support",
                "The project targets a framework that no longer receives security updates.",
                "Retarget to a supported framework; see the modernization roadmap for prerequisites.",
                Loc(new("Framework alvo fora de suporte",
                    "O projeto tem como alvo um framework que não recebe mais atualizações de segurança.",
                    "Mude o alvo para um framework suportado; veja os pré-requisitos no roadmap de modernização.",
                    "{framework} {version} — fora de suporte",
                    "{framework} {version} deixou de receber suporte em {endOfLife}."))),
            new(RuleIds.FrameworkEndingSoon, RulesVersion, FindingCategory.Modernization, Severity.Medium,
                "Target framework support ending soon",
                "The project targets a framework whose support ends within six months.",
                "Plan the upgrade to the next LTS before the end-of-support date.",
                Loc(new("Suporte do framework alvo termina em breve",
                    "O projeto tem como alvo um framework cujo suporte termina em até seis meses.",
                    "Planeje a atualização para o próximo LTS antes da data de fim de suporte.",
                    "{framework} {version} — suporte termina em {endOfLife}",
                    "O suporte de {framework} {version} termina em {endOfLife}."))),
            new(RuleIds.FrameworkLegacy, RulesVersion, FindingCategory.Modernization, Severity.Low,
                "Legacy target framework",
                "The framework is serviced but receives no new features (.NET Framework 4.7+, .NET Standard 1.x).",
                "Include in modernization scope; no immediate action required.",
                Loc(new("Framework alvo legado",
                    "O framework é mantido, mas não recebe novas funcionalidades (.NET Framework 4.7+, .NET Standard 1.x).",
                    "Inclua no escopo de modernização; sem ação imediata necessária.",
                    "{framework} {version} — legado",
                    "{framework} {version} é mantido, mas não evolui; candidato à modernização."))),
            new(RuleIds.FrameworkUnknown, RulesVersion, FindingCategory.Dependencies, Severity.Informational,
                "Target framework not recognized",
                "Atlas could not determine the project's target framework or has no lifecycle data for it.",
                "Verify the project file; report the moniker if it is valid.",
                Loc(new("Framework alvo não reconhecido",
                    "O Atlas não conseguiu determinar o framework alvo do projeto ou não tem dados de ciclo de vida para ele.",
                    "Verifique o arquivo de projeto; informe o moniker se ele for válido.",
                    "{framework} {version} — não reconhecido",
                    "Sem dados de ciclo de vida para o moniker '{moniker}'."))),
            new(RuleIds.NpmInventory, RulesVersion, FindingCategory.Dependencies, Severity.Informational,
                "npm packages resolved by a lockfile", "The front-end dependency footprint (exact versions from package-lock.json); vulnerable ones are reported separately.",
                null,
                new Dictionary<string, RuleLocalization>
                {
                    ["pt-BR"] = new("Pacotes npm resolvidos por lockfile", "A pegada de dependências de front-end (versões exatas do package-lock.json); os vulneráveis são reportados à parte.", null,
                        "{count} pacote(s) npm em {fileName}", "{count} pacote(s) npm ({dev} de desenvolvimento) em {file}."),
                }),
            new(RuleIds.VulnerablePackage, RulesVersion, FindingCategory.Security, Severity.High,
                "Package with known vulnerability",
                "A referenced NuGet package version is affected by a published vulnerability (OSV).",
                "Upgrade to the fixed version or later.",
                Loc(new("Pacote com vulnerabilidade conhecida",
                    "Uma versão de pacote NuGet referenciada é afetada por uma vulnerabilidade publicada (OSV).",
                    "Atualize para a versão corrigida ou posterior.",
                    "{ecosystemLabel}{package} {version}: {vulnerability}",
                    "{package} {version} é afetado por {vulnerability}. {fixedPt} Afeta {projectCount} projeto(s): {projects}."))),
            new(RuleIds.VersionConflict, RulesVersion, FindingCategory.Dependencies, Severity.Low,
                "Package version conflict across projects",
                "The same package is referenced at different versions by different projects.",
                "Align versions (Directory.Packages.props / central package management).",
                Loc(new("Conflito de versão de pacote entre projetos",
                    "O mesmo pacote é referenciado em versões diferentes por projetos diferentes.",
                    "Alinhe as versões (Directory.Packages.props / gerência central de pacotes).",
                    "{package} referenciado em {versionCount} versões",
                    "Versões {versions} em {projectCount} projeto(s): {projects}."))),
            new(RuleIds.UnresolvedProjectReference, RulesVersion, FindingCategory.Dependencies, Severity.Low,
                "Unresolved project reference",
                "A ProjectReference points to a project file that is not in the workspace.",
                "Fix the path or include the referenced project in the repository.",
                Loc(new("Referência de projeto não resolvida",
                    "Um ProjectReference aponta para um arquivo de projeto que não está no workspace.",
                    "Corrija o caminho ou inclua o projeto referenciado no repositório.",
                    "Referência não resolvida para {to}",
                    "{from} referencia {to}, que não está no workspace."))),
        };

        foreach (var blocker in MigrationBlockerRules.Catalog)
        {
            rules.Add(new RuleSpec(
                RuleIds.MigrationBlocker(blocker.Id), RulesVersion, FindingCategory.Modernization, ImpactSeverity(blocker.Impact),
                blocker.Title,
                $"Migration blocker {blocker.Id}: a dependency or project trait that blocks or complicates migration to modern .NET.",
                blocker.Remediation,
                Loc(new(blocker.TitlePtBr,
                    $"Blocker de migração {blocker.Id}: dependência ou característica do projeto que bloqueia ou complica a migração para o .NET moderno.",
                    blocker.RemediationPtBr,
                    blocker.TitlePtBr,
                    blocker.MessageTemplatePtBr))));
        }

        return rules;
    }

    private static IReadOnlyDictionary<string, RuleLocalization> Loc(RuleLocalization pt) =>
        new Dictionary<string, RuleLocalization> { [Pt] = pt };

    private static Severity ImpactSeverity(BlockerImpact impact) => impact switch
    {
        BlockerImpact.High => Severity.High,
        BlockerImpact.Medium => Severity.Medium,
        _ => Severity.Low,
    };

    public async Task<ScanResult> ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var projects = context.Languages.Values.SelectMany(l => l.Projects).ToList();

        // Front-end lockfiles ride along in most .NET repositories; read them as data too.
        var npm = new List<NpmPackage>();
        foreach (var lockfile in context.Workspace.SourceFiles(NpmLockfileParser.FileName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var packages = NpmLockfileParser.Parse(Normalize(lockfile), await context.Workspace.ReadAllTextAsync(lockfile, cancellationToken));
                npm.AddRange(packages);
                if (packages.Count > 0)
                {
                    context.Findings.Emit(new FindingCandidate(
                        RuleIds.NpmInventory, Severity.Informational, ConfidenceLevel.High,
                        Title: $"{packages.Count} npm package(s) in {Path.GetFileName(lockfile)}",
                        Message: $"{packages.Count} npm package(s) ({packages.Count(p => p.IsDev)} dev) in {Normalize(lockfile)}.",
                        Evidence: new EvidenceCandidate(FilePath: Normalize(lockfile), Symbol: "npm"),
                        Data: new Dictionary<string, string>
                        {
                            ["count"] = packages.Count.ToString(CultureInfo.InvariantCulture),
                            ["dev"] = packages.Count(p => p.IsDev).ToString(CultureInfo.InvariantCulture),
                            ["fileName"] = Path.GetFileName(lockfile),
                        }));
                }
            }
            catch (IOException)
            {
            }
        }

        if (projects.Count == 0 && npm.Count == 0)
        {
            return ScanResult.Success();
        }

        var analysis = await new DependencyAnalyzer(vulnerabilities)
            .AnalyzeAsync(projects, npm, context.Today, cancellationToken);

        EmitFrameworks(context, analysis);
        EmitBlockers(context, analysis);
        EmitVulnerabilities(context, analysis);
        EmitConflicts(context, analysis);
        EmitUnresolvedReferences(context, analysis);

        return ScanResult.Success();
    }

    private static void EmitFrameworks(ScanContext context, DependencyAnalysisResult analysis)
    {
        foreach (var framework in analysis.Frameworks)
        {
            var (ruleId, severity) = framework.Status switch
            {
                FrameworkSupportStatus.EndOfLife => (RuleIds.FrameworkEndOfLife, Severity.High),
                FrameworkSupportStatus.EndingSoon => (RuleIds.FrameworkEndingSoon, Severity.Medium),
                FrameworkSupportStatus.SupportedLegacy => (RuleIds.FrameworkLegacy, Severity.Low),
                FrameworkSupportStatus.Unknown => (RuleIds.FrameworkUnknown, Severity.Informational),
                _ => (null, Severity.Informational),
            };

            if (ruleId is null)
            {
                continue;
            }

            context.Findings.Emit(new FindingCandidate(
                ruleId,
                severity,
                ConfidenceLevel.High,
                Title: $"{framework.Framework} {framework.Version} — {framework.Status}",
                Message: framework.Explanation,
                Evidence: new EvidenceCandidate(FilePath: framework.ProjectPath, Symbol: framework.RawMoniker),
                Data: new Dictionary<string, string>
                {
                    ["framework"] = framework.Framework,
                    ["version"] = framework.Version ?? string.Empty,
                    ["moniker"] = framework.RawMoniker,
                    ["status"] = framework.Status.ToString(),
                    ["endOfLife"] = framework.EndOfLife?.ToString("yyyy-MM-dd") ?? string.Empty,
                    ["catalog"] = analysis.Catalogs.FrameworkSupport,
                }));
        }
    }

    private static void EmitBlockers(ScanContext context, DependencyAnalysisResult analysis)
    {
        foreach (var blocker in analysis.MigrationBlockers)
        {
            context.Findings.Emit(new FindingCandidate(
                RuleIds.MigrationBlocker(blocker.RuleId),
                ImpactSeverity(blocker.Impact),
                ConfidenceLevel.High,
                Title: blocker.Title,
                Message: $"{blocker.Evidence}. Impact: {blocker.Impact}.",
                Evidence: new EvidenceCandidate(FilePath: blocker.ProjectPath, Symbol: blocker.RuleId),
                Remediation: blocker.Remediation,
                Data: new Dictionary<string, string>
                {
                    ["blockerRule"] = blocker.RuleId,
                    ["impact"] = blocker.Impact.ToString(),
                    ["evidenceKind"] = blocker.StructuredEvidence.Kind,
                    ["evidenceValue"] = blocker.StructuredEvidence.Value,
                    ["count"] = blocker.StructuredEvidence.Count.ToString(CultureInfo.InvariantCulture),
                    ["catalog"] = analysis.Catalogs.MigrationRules,
                }));
        }
    }

    private static void EmitVulnerabilities(ScanContext context, DependencyAnalysisResult analysis)
    {
        foreach (var vulnerability in analysis.Vulnerabilities)
        {
            var severity = MapOsvSeverity(vulnerability.Severity);
            {
                // One finding per (package, version, vulnerability) across the estate; the projects are evidence data.
                var projects = vulnerability.Projects;
                context.Findings.Emit(new FindingCandidate(
                    RuleIds.VulnerablePackage,
                    severity,
                    ConfidenceLevel.High,
                    Title: $"{(vulnerability.Ecosystem == "NuGet" ? string.Empty : vulnerability.Ecosystem + ": ")}{vulnerability.PackageId} {vulnerability.Version}: {vulnerability.VulnerabilityId}",
                    Message: $"{vulnerability.Summary ?? $"Known vulnerability {vulnerability.VulnerabilityId}."} Affects {projects.Count} project(s): {string.Join(", ", projects)}.",
                    Evidence: new EvidenceCandidate(
                        FilePath: projects.FirstOrDefault(),
                        Symbol: $"{vulnerability.PackageId}@{vulnerability.Version}:{vulnerability.VulnerabilityId}"),
                    Remediation: vulnerability.FixedVersion is null
                        ? "No fixed version published; evaluate mitigation or replacement."
                        : $"Upgrade {vulnerability.PackageId} to {vulnerability.FixedVersion} or later.",
                    Data: new Dictionary<string, string>
                    {
                        ["package"] = vulnerability.PackageId,
                        ["version"] = vulnerability.Version,
                        ["vulnerability"] = vulnerability.VulnerabilityId,
                        ["aliases"] = string.Join(",", vulnerability.Aliases),
                        ["osvSeverity"] = vulnerability.Severity ?? string.Empty,
                        ["fixedVersion"] = vulnerability.FixedVersion ?? string.Empty,
                        ["fixedPt"] = vulnerability.FixedVersion is null
                            ? "Nenhuma versão corrigida publicada."
                            : $"Corrigido a partir da versão {vulnerability.FixedVersion}.",
                        ["bundle"] = analysis.Catalogs.VulnerabilityBundle ?? string.Empty,
                        ["projects"] = string.Join(", ", projects),
                        ["projectCount"] = projects.Count.ToString(CultureInfo.InvariantCulture),
                        ["ecosystem"] = vulnerability.Ecosystem,
                        ["ecosystemLabel"] = vulnerability.Ecosystem == "NuGet" ? string.Empty : vulnerability.Ecosystem + ": ",
                    }));
            }
        }
    }

    private static void EmitConflicts(ScanContext context, DependencyAnalysisResult analysis)
    {
        foreach (var package in analysis.Packages.Where(p => p.HasVersionConflict))
        {
            context.Findings.Emit(new FindingCandidate(
                RuleIds.VersionConflict,
                Severity.Low,
                ConfidenceLevel.High,
                Title: $"{package.Id} referenced at {package.Versions.Count} versions",
                Message: $"Versions {string.Join(", ", package.Versions)} across {package.Projects.Count} project(s): {string.Join(", ", package.Projects)}.",
                Evidence: new EvidenceCandidate(Symbol: package.Id),
                Data: new Dictionary<string, string>
                {
                    ["package"] = package.Id,
                    ["versions"] = string.Join(", ", package.Versions),
                    ["versionCount"] = package.Versions.Count.ToString(CultureInfo.InvariantCulture),
                    ["projectCount"] = package.Projects.Count.ToString(CultureInfo.InvariantCulture),
                    ["projects"] = string.Join(", ", package.Projects),
                }));
        }
    }

    private static void EmitUnresolvedReferences(ScanContext context, DependencyAnalysisResult analysis)
    {
        foreach (var edge in analysis.ProjectGraph.Edges.Where(e => !e.Resolved))
        {
            context.Findings.Emit(new FindingCandidate(
                RuleIds.UnresolvedProjectReference,
                Severity.Low,
                ConfidenceLevel.High,
                Title: $"Unresolved reference to {edge.To}",
                Message: $"{edge.From} references {edge.To}, which is not in the workspace.",
                Evidence: new EvidenceCandidate(FilePath: edge.From, Symbol: edge.To),
                Data: new Dictionary<string, string> { ["from"] = edge.From, ["to"] = edge.To }));
        }
    }

    /// <summary>Conservative mapping: unknown or vector-only severities land on Medium rather than over-claiming.</summary>
    private static Severity MapOsvSeverity(string? severity) =>
        severity?.Trim().ToUpperInvariant() switch
        {
            "CRITICAL" => Severity.Critical,
            "HIGH" => Severity.High,
            "MODERATE" or "MEDIUM" => Severity.Medium,
            "LOW" => Severity.Low,
            _ => Severity.Medium,
        };

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('.', '/');
}
