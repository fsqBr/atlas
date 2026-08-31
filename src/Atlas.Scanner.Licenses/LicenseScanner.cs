using System.Text.Json;
using Atlas.Domain.Findings;
using Atlas.Domain.Rules;
using Atlas.Domain.Workspaces;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;
using Atlas.Scanner.Dependencies;

namespace Atlas.Scanner.Licenses;

/// <summary>
/// License compliance for the dependency footprint (NuGet from project facts, npm
/// from lockfiles): classifies every package, flags copyleft/restricted/denied ones
/// and publishes the component list that the SBOM export (CycloneDX) is built from.
/// </summary>
public sealed class LicenseScanner(ILicenseResolver resolver, LicenseOptions options) : IScanner
{
    public static class RuleIds
    {
        public const string Inventory = "license.inventory";
        public const string StrongCopyleft = "license.strong-copyleft";
        public const string WeakCopyleft = "license.weak-copyleft";
        public const string Restricted = "license.restricted";
        public const string Denied = "license.denied";
        public const string Unknown = "license.unknown";
    }

    private const string RulesVersion = "1.0.0";
    private const string Pt = "pt-BR";
    private const int MaxPerRule = 200;
    public const int MaxComponentsInData = 3000;

    private static IReadOnlyDictionary<string, RuleLocalization> Loc(RuleLocalization pt) => new Dictionary<string, RuleLocalization> { [Pt] = pt };

    public ScannerDescriptor Descriptor { get; } = new(
        Id: "license.compliance",
        Name: "License Compliance & SBOM",
        Version: "0.1.0",
        Category: FindingCategory.Dependencies,
        Capabilities: ["license-classification", "sbom-components", "policy-denylist"]);

    public IReadOnlyList<RuleSpec> Rules { get; } =
    [
        new(RuleIds.Inventory, RulesVersion, FindingCategory.Dependencies, Severity.Informational,
            "Dependency licenses", "Every third-party package with its license class; the SBOM export is built from this list.",
            null,
            Loc(new("Licenças das dependências", "Todos os pacotes de terceiros com sua classe de licença; o export SBOM é montado a partir desta lista.", null,
                "Licenças das dependências", "{total} pacote(s): {permissive} permissivo(s), {weak} copyleft fraco, {strong} copyleft forte, {restricted} restrito(s), {unknown} desconhecido(s)."))),
        new(RuleIds.StrongCopyleft, RulesVersion, FindingCategory.Dependencies, Severity.High,
            "Strong copyleft license in a dependency", "GPL/AGPL-family licenses can require releasing your own source when the software is distributed (or, for AGPL, offered over a network). Needs legal review before shipping.",
            "Confirm the usage (linking, distribution, SaaS), obtain a commercial license or replace the package.",
            Loc(new("Licença copyleft forte em dependência", "Licenças da família GPL/AGPL podem exigir a liberação do seu próprio código quando o software é distribuído (ou, no AGPL, oferecido pela rede). Precisa de revisão jurídica antes de entregar.",
                "Confirme o uso (linking, distribuição, SaaS), obtenha licença comercial ou substitua o pacote.",
                "{id} {version}: {license}", "{ecosystem} {id} {version} está sob {license} ({class})."))),
        new(RuleIds.WeakCopyleft, RulesVersion, FindingCategory.Dependencies, Severity.Low,
            "Weak copyleft license in a dependency", "LGPL/MPL/EPL-style licenses are usually fine when the package is used unmodified, but changes to it must be published and attribution kept.",
            "Keep the package unmodified and attributions in place; document it in the third-party notices.",
            Loc(new("Licença copyleft fraca em dependência", "Licenças tipo LGPL/MPL/EPL costumam ser aceitáveis quando o pacote é usado sem alterações, mas modificações precisam ser publicadas e a atribuição mantida.",
                "Mantenha o pacote sem alterações e as atribuições; documente nas notices de terceiros.",
                "{id} {version}: {license}", "{ecosystem} {id} {version} está sob {license} ({class})."))),
        new(RuleIds.Restricted, RulesVersion, FindingCategory.Dependencies, Severity.High,
            "Restricted or non-commercial license in a dependency", "Source-available, non-commercial or proprietary terms (SSPL, BUSL, CC-BY-NC, EULA) restrict how the software may be used or sold.",
            "Check the terms against how the product is offered; buy a license or replace the package.",
            Loc(new("Licença restrita ou não comercial em dependência", "Termos source-available, não comerciais ou proprietários (SSPL, BUSL, CC-BY-NC, EULA) restringem como o software pode ser usado ou vendido.",
                "Confronte os termos com a forma de oferta do produto; compre licença ou substitua o pacote.",
                "{id} {version}: {license}", "{ecosystem} {id} {version} está sob {license} ({class})."))),
        new(RuleIds.Denied, RulesVersion, FindingCategory.Dependencies, Severity.Critical,
            "License denied by policy", "The organisation's license policy (Atlas:Licenses:Denied) forbids this license in shipped software.",
            "Replace the package or obtain an exception from legal; record the decision as a suppression with reason.",
            Loc(new("Licença proibida pela política", "A política de licenças da organização (Atlas:Licenses:Denied) proíbe esta licença em software entregue.",
                "Substitua o pacote ou obtenha exceção do jurídico; registre a decisão como supressão com motivo.",
                "{id} {version}: {license} (proibida)", "{ecosystem} {id} {version} está sob {license}, proibida pela política."))),
        new(RuleIds.Unknown, RulesVersion, FindingCategory.Dependencies, Severity.Informational,
            "Dependencies without a resolvable license", "No SPDX expression could be determined (no metadata, custom file, or lookup not performed). Unknown terms are a due-diligence gap.",
            "Check the package page or its LICENSE file and record the result; consider a suppression once verified.",
            Loc(new("Dependências sem licença identificável", "Nenhuma expressão SPDX pôde ser determinada (sem metadados, arquivo próprio ou consulta não realizada). Termos desconhecidos são uma lacuna de due diligence.",
                "Verifique a página do pacote ou o arquivo LICENSE e registre; considere uma supressão após verificar.",
                "{count} pacote(s) sem licença identificável", "{list}"))),
    ];

    public async Task<ScanResult> ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var packages = new List<(string Ecosystem, string Id, string Version)>();
        foreach (var project in context.Languages.Values.SelectMany(l => l.Projects))
        {
            foreach (var p in project.PackageReferences.Where(p => !string.IsNullOrWhiteSpace(p.Version) && p.Id != "?"))
            {
                packages.Add(("nuget", p.Id, CleanVersion(p.Version!)));
            }
        }

        foreach (var lockfile in context.Workspace.SourceFiles(NpmLockfileParser.FileName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var p in NpmLockfileParser.Parse(lockfile, await context.Workspace.ReadAllTextAsync(lockfile, cancellationToken)).Where(p => !p.IsDev))
                {
                    packages.Add(("npm", p.Name, p.Version));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // an unreadable lockfile is reported by the dependency scanner; nothing to add here
            }
        }

        var distinct = packages.Where(p => !p.Version.Contains('*') && !p.Version.Contains('[') && !p.Version.Contains('(')).Distinct().ToList();
        if (distinct.Count == 0)
        {
            return ScanResult.Success();
        }

        var licenses = await resolver.ResolveAsync(distinct, cancellationToken);
        var denied = new HashSet<string>(options.Denied.Select(d => d.Trim()), StringComparer.OrdinalIgnoreCase);

        var counts = Enum.GetValues<LicenseClass>().ToDictionary(c => c, c => licenses.Count(l => l.Class == c));
        var components = licenses.Take(MaxComponentsInData).Select(l => new { l.Ecosystem, l.Id, l.Version, License = l.Expression, Class = l.Class.ToString() });
        context.Findings.Emit(new FindingCandidate(RuleIds.Inventory, Severity.Informational, ConfidenceLevel.High,
            Title: "Dependency licenses",
            Message: $"{licenses.Count} package(s): {counts[LicenseClass.Permissive]} permissive, {counts[LicenseClass.WeakCopyleft]} weak copyleft, {counts[LicenseClass.StrongCopyleft]} strong copyleft, {counts[LicenseClass.Restricted]} restricted, {counts[LicenseClass.Unknown]} unknown.",
            Evidence: new EvidenceCandidate(Symbol: "licenses"),
            Data: new Dictionary<string, string>
            {
                ["total"] = licenses.Count.ToString(), ["permissive"] = counts[LicenseClass.Permissive].ToString(), ["weak"] = counts[LicenseClass.WeakCopyleft].ToString(),
                ["strong"] = counts[LicenseClass.StrongCopyleft].ToString(), ["restricted"] = counts[LicenseClass.Restricted].ToString(), ["unknown"] = counts[LicenseClass.Unknown].ToString(),
                ["components"] = JsonSerializer.Serialize(components),
            }));

        var emitted = 0;
        foreach (var l in licenses.Where(l => l.Class is LicenseClass.StrongCopyleft or LicenseClass.WeakCopyleft or LicenseClass.Restricted || IsDenied(l, denied))
                     .OrderByDescending(l => IsDenied(l, denied)).ThenByDescending(l => l.Class))
        {
            if (emitted++ >= MaxPerRule * 3)
            {
                break;
            }

            var isDenied = IsDenied(l, denied);
            var ruleId = isDenied ? RuleIds.Denied : l.Class switch
            {
                LicenseClass.StrongCopyleft => RuleIds.StrongCopyleft,
                LicenseClass.Restricted => RuleIds.Restricted,
                _ => RuleIds.WeakCopyleft,
            };
            var rule = Rules.First(r => r.Id == ruleId);
            var license = l.Expression ?? "?";
            context.Findings.Emit(new FindingCandidate(ruleId, rule.DefaultSeverity, ConfidenceLevel.Medium,
                Title: $"{l.Id} {l.Version}: {license}{(isDenied ? " (denied)" : "")}",
                Message: isDenied ? $"{l.Ecosystem} {l.Id} {l.Version} is under {license}, denied by policy." : $"{l.Ecosystem} {l.Id} {l.Version} is under {license} ({l.Class}).",
                Evidence: new EvidenceCandidate(Symbol: $"{l.Ecosystem}:{l.Id}"),
                Remediation: rule.Remediation,
                Data: new Dictionary<string, string> { ["ecosystem"] = l.Ecosystem, ["id"] = l.Id, ["version"] = l.Version, ["license"] = license, ["class"] = l.Class.ToString() }));
        }

        var unknown = licenses.Where(l => l.Class == LicenseClass.Unknown).ToList();
        if (unknown.Count > 0)
        {
            var list = string.Join(", ", unknown.Take(40).Select(u => $"{u.Id}@{u.Version}")) + (unknown.Count > 40 ? $" … (+{unknown.Count - 40})" : "");
            context.Findings.Emit(new FindingCandidate(RuleIds.Unknown, Severity.Informational, ConfidenceLevel.Medium,
                Title: $"{unknown.Count} package(s) without a resolvable license",
                Message: list,
                Evidence: new EvidenceCandidate(Symbol: "licenses.unknown"),
                Remediation: Rules.First(r => r.Id == RuleIds.Unknown).Remediation,
                Data: new Dictionary<string, string> { ["count"] = unknown.Count.ToString(), ["list"] = list }));
        }

        return ScanResult.Success();
    }

    private static bool IsDenied(PackageLicense l, HashSet<string> denied) =>
        denied.Count > 0 && (denied.Contains(l.Class.ToString()) || (l.Expression is not null && (denied.Contains(l.Expression) || l.Expression.Split([" OR ", " AND "], StringSplitOptions.TrimEntries).Any(denied.Contains))));

    private static string CleanVersion(string version) => version.Trim().TrimStart('[').TrimEnd(']', ')').Split(',')[0].Trim();
}
