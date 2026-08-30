using Atlas.Language.Abstractions;

namespace Atlas.Scanner.Dependencies;

public sealed record BlockerEvidence(string Kind, string Value, int Count = 0);

public sealed record BlockerRule(
    string Id,
    BlockerImpact Impact,
    string Title,
    string Remediation,
    string TitlePtBr,
    string RemediationPtBr,
    string MessageTemplatePtBr);

/// <summary>
/// Curated, versioned rules for ".NET Framework → modern .NET" migration
/// blockers, evaluated against project facts only (no code execution). Each hit
/// carries the evidence that triggered it and a concrete remediation direction.
/// Text is bilingual by design: findings are data, words are rendered per reader.
/// </summary>
public static class MigrationBlockerRules
{
    public const string Version = "2026.08";

    private sealed record Rule(BlockerRule Spec, Func<ProjectFact, BlockerEvidence?> Match);

    private static readonly IReadOnlyList<Rule> Rules =
    [
        new(new("MB-001", BlockerImpact.Prerequisite,
            "Legacy (non-SDK) project format",
            "Convert to SDK-style csproj (try-convert / .NET Upgrade Assistant) before any framework change.",
            "Formato de projeto legado (não-SDK)",
            "Converta para csproj SDK-style (try-convert / .NET Upgrade Assistant) antes de qualquer mudança de framework.",
            "O csproj não tem atributo Sdk. Impacto: pré-requisito."),
            p => p.IsSdkStyle ? null : new BlockerEvidence("format", "csproj has no Sdk attribute")),

        new(new("MB-002", BlockerImpact.Prerequisite,
            "packages.config dependency management",
            "Migrate to PackageReference; packages.config is not supported by SDK-style projects.",
            "Gerência de dependências via packages.config",
            "Migre para PackageReference; packages.config não é suportado por projetos SDK-style.",
            "packages.config com {count} pacote(s). Impacto: pré-requisito."),
            p => p.PackageReferences.Count(r => r.Origin == PackageReferenceOrigin.PackagesConfig) is var n and > 0
                ? new BlockerEvidence("packagesConfig", $"packages.config with {n} package(s)", n)
                : null),

        new(new("MB-003", BlockerImpact.High,
            "ASP.NET classic (System.Web) — WebForms/HttpModules",
            "System.Web does not exist on modern .NET. WebForms requires a rewrite (Razor Pages/Blazor); MVC/Web API move to ASP.NET Core.",
            "ASP.NET clássico (System.Web) — WebForms/HttpModules",
            "System.Web não existe no .NET moderno. WebForms exige reescrita (Razor Pages/Blazor); MVC/Web API migram para ASP.NET Core.",
            "Referência: {evidenceValue}. Impacto: alto."),
            p => Assembly(p, "System.Web")),

        new(new("MB-004", BlockerImpact.Medium,
            "ASP.NET MVC 5",
            "Port controllers/views to ASP.NET Core MVC; routing, filters and DI change shape.",
            "ASP.NET MVC 5",
            "Porte controllers/views para ASP.NET Core MVC; roteamento, filtros e DI mudam de forma.",
            "Pacote: {evidenceValue}. Impacto: médio."),
            p => Package(p, "Microsoft.AspNet.Mvc")),

        new(new("MB-005", BlockerImpact.Medium,
            "ASP.NET Web API 2",
            "Port to ASP.NET Core controllers; message handlers and OWIN hosting are replaced by middleware.",
            "ASP.NET Web API 2",
            "Porte para controllers do ASP.NET Core; message handlers e hosting OWIN são substituídos por middleware.",
            "Pacote: {evidenceValue}. Impacto: médio."),
            p => PackagePrefix(p, "Microsoft.AspNet.WebApi")),

        new(new("MB-006", BlockerImpact.Medium,
            "Entity Framework 6",
            "EF6 runs on modern .NET as a bridge (EF6.3+); plan the EF Core migration for LINQ/provider differences.",
            "Entity Framework 6",
            "EF6 roda no .NET moderno como ponte (EF6.3+); planeje a migração para EF Core pelas diferenças de LINQ/provider.",
            "Pacote: {evidenceValue}. Impacto: médio."),
            p => Package(p, "EntityFramework")),

        new(new("MB-007", BlockerImpact.High,
            "WCF (System.ServiceModel)",
            "Server-side WCF needs CoreWCF or a redesign to gRPC/REST; client proxies work via System.ServiceModel.* packages.",
            "WCF (System.ServiceModel)",
            "WCF no servidor exige CoreWCF ou redesenho para gRPC/REST; proxies de cliente funcionam via pacotes System.ServiceModel.*.",
            "Referência: {evidenceValue}. Impacto: alto."),
            p => Assembly(p, "System.ServiceModel")),

        new(new("MB-008", BlockerImpact.High,
            ".NET Remoting",
            "No modern equivalent; replace with gRPC, HTTP APIs or messaging.",
            ".NET Remoting",
            "Sem equivalente moderno; substitua por gRPC, APIs HTTP ou mensageria.",
            "Referência: {evidenceValue}. Impacto: alto."),
            p => Assembly(p, "System.Runtime.Remoting")),

        new(new("MB-009", BlockerImpact.High,
            "Windows Workflow Foundation",
            "Not available on modern .NET; evaluate CoreWF (community) or redesign workflows.",
            "Windows Workflow Foundation",
            "Indisponível no .NET moderno; avalie CoreWF (comunidade) ou redesenhe os workflows.",
            "Referência: {evidenceValue}. Impacto: alto."),
            p => AssemblyPrefix(p, "System.Activities") ?? AssemblyPrefix(p, "System.Workflow")),

        new(new("MB-010", BlockerImpact.High,
            "MSMQ (System.Messaging)",
            "Not available on modern .NET; replace with a supported broker (RabbitMQ, Azure Service Bus, …).",
            "MSMQ (System.Messaging)",
            "Indisponível no .NET moderno; substitua por um broker suportado (RabbitMQ, Azure Service Bus, …).",
            "Referência: {evidenceValue}. Impacto: alto."),
            p => Assembly(p, "System.Messaging")),

        new(new("MB-011", BlockerImpact.Medium,
            "Windows-only desktop UI (WinForms/WPF)",
            "Supported on modern .NET with the -windows TFM, but locks the project to Windows.",
            "UI desktop somente Windows (WinForms/WPF)",
            "Suportado no .NET moderno com o TFM -windows, mas prende o projeto ao Windows.",
            "Referência: {evidenceValue}. Impacto: médio."),
            p => Assembly(p, "System.Windows.Forms") ?? Assembly(p, "PresentationFramework")),

        new(new("MB-012", BlockerImpact.Medium,
            "OWIN/Katana hosting",
            "Replace OWIN middleware and startup with the ASP.NET Core middleware pipeline.",
            "Hosting OWIN/Katana",
            "Substitua middleware e startup OWIN pelo pipeline de middleware do ASP.NET Core.",
            "Pacote: {evidenceValue}. Impacto: médio."),
            p => PackagePrefix(p, "Microsoft.Owin")),

        new(new("MB-013", BlockerImpact.Medium,
            "Enterprise Library",
            "Unmaintained; replace blocks (logging, data access, validation) with modern equivalents.",
            "Enterprise Library",
            "Sem manutenção; substitua os blocos (logging, acesso a dados, validação) por equivalentes modernos.",
            "Pacote: {evidenceValue}. Impacto: médio."),
            p => PackagePrefix(p, "EnterpriseLibrary")),
    ];

    /// <summary>All rules with their bilingual text — the scanner turns these into catalog entries.</summary>
    public static IReadOnlyList<BlockerRule> Catalog => Rules.Select(r => r.Spec).ToList();

    public static IReadOnlyList<MigrationBlocker> Evaluate(ProjectFact project)
    {
        var blockers = new List<MigrationBlocker>();
        foreach (var rule in Rules)
        {
            var evidence = rule.Match(project);
            if (evidence is not null)
            {
                blockers.Add(new MigrationBlocker(
                    rule.Spec.Id, project.RelativePath, rule.Spec.Impact, rule.Spec.Title,
                    FormatEvidence(evidence), rule.Spec.Remediation, evidence));
            }
        }

        return blockers;
    }

    private static string FormatEvidence(BlockerEvidence e) => e.Kind switch
    {
        "reference" => $"Reference: {e.Value}",
        "package" => $"Package: {e.Value}",
        _ => e.Value,
    };

    private static BlockerEvidence? Assembly(ProjectFact p, string name) =>
        p.AssemblyReferences.Any(r => r.Equals(name, StringComparison.OrdinalIgnoreCase))
            ? new BlockerEvidence("reference", name)
            : null;

    private static BlockerEvidence? AssemblyPrefix(ProjectFact p, string prefix)
    {
        var hit = p.AssemblyReferences.FirstOrDefault(r => r.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return hit is null ? null : new BlockerEvidence("reference", hit);
    }

    private static BlockerEvidence? Package(ProjectFact p, string id)
    {
        var hit = p.PackageReferences.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        return hit is null ? null : new BlockerEvidence("package", $"{hit.Id} {hit.Version}".TrimEnd());
    }

    private static BlockerEvidence? PackagePrefix(ProjectFact p, string prefix)
    {
        var hit = p.PackageReferences.FirstOrDefault(r => r.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return hit is null ? null : new BlockerEvidence("package", $"{hit.Id} {hit.Version}".TrimEnd());
    }
}
