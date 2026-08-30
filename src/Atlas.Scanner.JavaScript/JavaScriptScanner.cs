using System.Text.Json;
using System.Text.RegularExpressions;
using Atlas.Domain.Findings;
using Atlas.Domain.Rules;
using Atlas.Domain.Workspaces;
using Atlas.Scanner.Abstractions;

namespace Atlas.Scanner.JavaScript;

/// <summary>
/// Front-end footprint of a .NET estate (.7): which JavaScript
/// frameworks the UI depends on (from package.json, bower.json and script tags in
/// Razor/WebForms/HTML views), legacy ones that block a UI modernization, and a
/// few high-signal unsafe patterns in hand-written JS/TS. Text-only, no bundler,
/// no execution.
/// </summary>
public sealed partial class JavaScriptScanner : IScanner
{
    public static class RuleIds
    {
        public const string Inventory = "javascript.inventory";
        public const string LegacyFramework = "javascript.legacy-framework";
        public const string Eval = "javascript.eval";
        public const string DomInjection = "javascript.dom-injection";
        public const string InsecureUrl = "javascript.insecure-url";
    }

    private const string RulesVersion = "1.0.0";
    private const string Pt = "pt-BR";
    private const int MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxFindingsPerRule = 200;

    private static IReadOnlyDictionary<string, RuleLocalization> Loc(RuleLocalization pt) => new Dictionary<string, RuleLocalization> { [Pt] = pt };

    /// <summary>Package name → (display name, legacy?). Legacy = end-of-life or no migration path to a maintained major.</summary>
    private static readonly (string Package, string Name, bool Legacy)[] KnownFrameworks =
    [
        ("react", "React", false), ("vue", "Vue", false), ("@angular/core", "Angular", false), ("svelte", "Svelte", false), ("next", "Next.js", false),
        ("jquery", "jQuery", false), ("angular", "AngularJS 1.x", true), ("knockout", "Knockout", true), ("backbone", "Backbone", true),
        ("ember-source", "Ember", false), ("bootstrap", "Bootstrap", false), ("moment", "Moment.js (deprecated)", true), ("kendo-ui", "Kendo UI", false),
        ("@microsoft/signalr", "SignalR", false), ("typescript", "TypeScript", false), ("webpack", "webpack", false), ("vite", "Vite", false),
        ("gulp", "gulp", true), ("grunt", "Grunt", true), ("bower", "Bower", true),
    ];

    [GeneratedRegex(@"<script[^>]+src\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTag();

    [GeneratedRegex(@"(jquery-1\.\d|jquery-2\.\d|jquery\.min\.js|angular(\.min)?\.js|knockout|backbone|prototype\.js|mootools|dojo\.js|ext-all|MicrosoftAjax)", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyScript();

    [GeneratedRegex(@"(?<![\w.])(eval\s*\(|new\s+Function\s*\(|setTimeout\s*\(\s*[""']|setInterval\s*\(\s*[""'])")]
    private static partial Regex EvalPattern();

    [GeneratedRegex(@"(\.innerHTML\s*[+]?=\s*[^;]*(\+|\$\{)|document\.write(ln)?\s*\([^)]*(\+|\$\{)|\.html\(\s*[^)]*(\+|\$\{)|dangerouslySetInnerHTML)")]
    private static partial Regex DomInjectionPattern();

    [GeneratedRegex(@"(fetch|axios\.(get|post|put|delete|request)|\$\.(ajax|get|post|getJSON)|XMLHttpRequest|open)\s*\([^)]*[""']http://(?!localhost|127\.0\.0\.1)", RegexOptions.IgnoreCase)]
    private static partial Regex InsecureUrlPattern();

    public ScannerDescriptor Descriptor { get; } = new(
        Id: "javascript.frontend",
        Name: "JavaScript / TypeScript Scanner",
        Version: "0.1.0",
        Category: FindingCategory.Quality,
        Capabilities: ["framework-inventory", "legacy-frameworks", "unsafe-patterns"]);

    public IReadOnlyList<RuleSpec> Rules { get; } =
    [
        new(RuleIds.Inventory, RulesVersion, FindingCategory.Quality, Severity.Informational,
            "Front-end footprint", "JavaScript/TypeScript frameworks and tooling the estate depends on, from package manifests and script tags.",
            null,
            Loc(new("Pegada de front-end", "Frameworks e ferramentas JavaScript/TypeScript de que o sistema depende, a partir de manifestos e tags de script.", null,
                "Pegada de front-end", "{files} arquivo(s) JS/TS, {manifests} manifesto(s); frameworks: {frameworks}."))),
        new(RuleIds.LegacyFramework, RulesVersion, FindingCategory.Modernization, Severity.Medium,
            "Legacy front-end framework", "An end-of-life or unmaintained front-end framework (AngularJS 1.x, Knockout, Backbone, jQuery 1.x/2.x bundles, Bower/Grunt tooling) is part of the UI: it has no upgrade path and drives the cost of a UI modernization.",
            "Inventory the screens that depend on it; plan a strangler migration of the UI layer (component by component) to a maintained framework.",
            Loc(new("Framework de front-end legado", "Um framework de front-end sem suporte (AngularJS 1.x, Knockout, Backbone, bundles jQuery 1.x/2.x, Bower/Grunt) faz parte da UI: não tem caminho de upgrade e puxa o custo da modernização da interface.",
                "Inventarie as telas que dependem dele; planeje a migração estrangulada da UI (componente a componente) para um framework mantido.",
                "Framework legado: {name}", "{name} referenciado em {where} ({count} ocorrência(s))."))),
        new(RuleIds.Eval, RulesVersion, FindingCategory.Security, Severity.Medium,
            "Dynamic code execution in JavaScript", "eval / new Function / string-based timers execute text as code: injection surface and a blocker for strict Content-Security-Policy.",
            "Replace with explicit parsing (JSON.parse) or function references; enable a CSP without 'unsafe-eval'.",
            Loc(new("Execução dinâmica de código em JavaScript", "eval / new Function / timers com string executam texto como código: superfície de injeção e bloqueio para Content-Security-Policy estrita.",
                "Substitua por parsing explícito (JSON.parse) ou referências de função; habilite CSP sem 'unsafe-eval'.",
                "Execução dinâmica: {fileName}", "{snippet} — {fileName} (linha {line})."))),
        new(RuleIds.DomInjection, RulesVersion, FindingCategory.Security, Severity.High,
            "HTML built from data written to the DOM", "innerHTML / document.write / .html() with concatenated or templated values (or dangerouslySetInnerHTML) render untrusted data as markup: classic DOM XSS.",
            "Use textContent or a templating framework's escaping; sanitize with a maintained library when HTML is unavoidable.",
            Loc(new("HTML montado com dados escrito no DOM", "innerHTML / document.write / .html() com valores concatenados ou interpolados (ou dangerouslySetInnerHTML) renderizam dados não confiáveis como markup: XSS de DOM clássico.",
                "Use textContent ou o escaping do framework de templates; sanitize com biblioteca mantida quando HTML for inevitável.",
                "Injeção no DOM: {fileName}", "{snippet} — {fileName} (linha {line})."))),
        new(RuleIds.InsecureUrl, RulesVersion, FindingCategory.Security, Severity.Medium,
            "Plain-HTTP endpoint called from the browser", "A request to an http:// URL from client code sends data in clear text and breaks mixed-content rules on HTTPS pages.",
            "Use https:// endpoints (or relative URLs) and enforce HSTS on the API.",
            Loc(new("Endpoint HTTP sem TLS chamado do navegador", "Uma requisição para URL http:// a partir do cliente envia dados em claro e quebra as regras de mixed content em páginas HTTPS.",
                "Use endpoints https:// (ou URLs relativas) e aplique HSTS na API.",
                "URL sem TLS: {fileName}", "{snippet} — {fileName} (linha {line})."))),
    ];

    public async Task<ScanResult> ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var frameworks = new Dictionary<string, (bool Legacy, int Count, HashSet<string> Where)>(StringComparer.Ordinal);
        var manifests = 0;

        foreach (var manifest in context.Workspace.SourceFiles("package.json").Concat(context.Workspace.SourceFiles("bower.json")))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await ReadAsync(context.Workspace, manifest, cancellationToken);
            if (text is null)
            {
                continue;
            }

            manifests++;
            foreach (var (package, name, legacy) in ReadDependencies(text))
            {
                Add(frameworks, name, legacy || manifest.EndsWith("bower.json", StringComparison.OrdinalIgnoreCase), manifest);
                _ = package;
            }
        }

        var viewPatterns = new[] { "*.cshtml", "*.aspx", "*.ascx", "*.master", "*.html", "*.vbhtml" };
        foreach (var view in viewPatterns.SelectMany(context.Workspace.SourceFiles).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await ReadAsync(context.Workspace, view, cancellationToken);
            if (text is null)
            {
                continue;
            }

            foreach (Match tag in ScriptTag().Matches(text))
            {
                var src = tag.Groups[1].Value;
                var legacy = LegacyScript().Match(src);
                if (legacy.Success)
                {
                    Add(frameworks, NameOfScript(src), true, view);
                }
                else if (src.Contains("jquery", StringComparison.OrdinalIgnoreCase))
                {
                    Add(frameworks, "jQuery", false, view);
                }
            }
        }

        var jsFiles = 0;
        var emitted = new Dictionary<string, int> { [RuleIds.Eval] = 0, [RuleIds.DomInjection] = 0, [RuleIds.InsecureUrl] = 0 };
        foreach (var path in new[] { "*.js", "*.ts", "*.jsx", "*.tsx", "*.mjs" }.SelectMany(context.Workspace.SourceFiles).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsVendorOrGenerated(path))
            {
                continue;
            }

            var text = await ReadAsync(context.Workspace, path, cancellationToken);
            if (text is null)
            {
                continue;
            }

            jsFiles++;
            Scan(context, path, text, EvalPattern(), RuleIds.Eval, Severity.Medium, emitted);
            Scan(context, path, text, DomInjectionPattern(), RuleIds.DomInjection, Severity.High, emitted);
            Scan(context, path, text, InsecureUrlPattern(), RuleIds.InsecureUrl, Severity.Medium, emitted);
        }

        if (manifests == 0 && jsFiles == 0 && frameworks.Count == 0)
        {
            return ScanResult.Success();
        }

        var frameworkList = frameworks.OrderByDescending(kv => kv.Value.Legacy).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
        context.Findings.Emit(new FindingCandidate(RuleIds.Inventory, Severity.Informational, ConfidenceLevel.High,
            Title: "Front-end footprint",
            Message: $"{jsFiles} JS/TS file(s), {manifests} manifest(s); frameworks: {(frameworkList.Count == 0 ? "none detected" : string.Join(", ", frameworkList.Select(kv => kv.Key + (kv.Value.Legacy ? " (legacy)" : ""))))}.",
            Evidence: new EvidenceCandidate(Symbol: "frontend"),
            Data: new Dictionary<string, string>
            {
                ["files"] = jsFiles.ToString(), ["manifests"] = manifests.ToString(),
                ["frameworks"] = frameworkList.Count == 0 ? "—" : string.Join(", ", frameworkList.Select(kv => kv.Key)),
                ["legacyFrameworks"] = string.Join(", ", frameworkList.Where(kv => kv.Value.Legacy).Select(kv => kv.Key)),
            }));

        foreach (var (name, (_, count, where)) in frameworkList.Where(kv => kv.Value.Legacy).Select(kv => (kv.Key, kv.Value)))
        {
            var first = where.OrderBy(w => w, StringComparer.OrdinalIgnoreCase).First();
            context.Findings.Emit(new FindingCandidate(RuleIds.LegacyFramework, Severity.Medium, ConfidenceLevel.High,
                Title: $"Legacy framework: {name}",
                Message: $"{name} referenced in {first}{(where.Count > 1 ? $" and {where.Count - 1} more file(s)" : "")} ({count} occurrence(s)).",
                Evidence: new EvidenceCandidate(FilePath: first, Symbol: $"frontend:{name}"),
                Remediation: Rules.First(r => r.Id == RuleIds.LegacyFramework).Remediation,
                Data: new Dictionary<string, string> { ["name"] = name, ["where"] = first, ["count"] = count.ToString() }));
        }

        return ScanResult.Success();
    }

    private void Scan(ScanContext context, string path, string text, Regex pattern, string ruleId, Severity severity, Dictionary<string, int> emitted)
    {
        foreach (Match match in pattern.Matches(text))
        {
            if (emitted[ruleId] >= MaxFindingsPerRule)
            {
                return;
            }

            var line = text.AsSpan(0, match.Index).Count('\n') + 1;
            var lineText = LineAt(text, match.Index).Trim();
            var snippet = lineText.Length > 120 ? lineText[..120] + "…" : lineText;
            emitted[ruleId]++;
            var rule = Rules.First(r => r.Id == ruleId);
            context.Findings.Emit(new FindingCandidate(ruleId, severity, ConfidenceLevel.Medium,
                Title: $"{rule.Title}: {Path.GetFileName(path)}",
                Message: $"{snippet} — {Path.GetFileName(path)} (line {line}).",
                Evidence: new EvidenceCandidate(FilePath: path, LineStart: line, Symbol: $"{Path.GetFileName(path)}:{ruleId}", SnippetHash: Fingerprint(snippet)),
                Remediation: rule.Remediation,
                Data: new Dictionary<string, string> { ["snippet"] = snippet, ["fileName"] = Path.GetFileName(path), ["line"] = line.ToString() }));
        }
    }

    internal static IEnumerable<(string Package, string Name, bool Legacy)> ReadDependencies(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (doc)
        {
            foreach (var section in new[] { "dependencies", "devDependencies" })
            {
                if (!doc.RootElement.TryGetProperty(section, out var deps) || deps.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var dep in deps.EnumerateObject())
                {
                    var known = KnownFrameworks.FirstOrDefault(k => k.Package.Equals(dep.Name, StringComparison.OrdinalIgnoreCase));
                    if (known.Name is null)
                    {
                        continue;
                    }

                    var version = dep.Value.ValueKind == JsonValueKind.String ? dep.Value.GetString() ?? "" : "";
                    var legacy = known.Legacy || (known.Package == "jquery" && (version.TrimStart('^', '~', '=', 'v').StartsWith("1.") || version.TrimStart('^', '~', '=', 'v').StartsWith("2.")));
                    yield return (known.Package, known.Package == "jquery" && legacy ? "jQuery 1.x/2.x" : known.Name, legacy);
                }
            }
        }
    }

    private static string NameOfScript(string src)
    {
        var file = Path.GetFileName(src).ToLowerInvariant();
        if (file.StartsWith("jquery-1.") || file.StartsWith("jquery-2.")) return "jQuery 1.x/2.x";
        if (file.StartsWith("jquery")) return "jQuery (bundled)";
        if (file.StartsWith("angular")) return "AngularJS 1.x";
        if (file.StartsWith("knockout")) return "Knockout";
        if (file.StartsWith("backbone")) return "Backbone";
        if (file.StartsWith("prototype")) return "Prototype.js";
        if (file.StartsWith("mootools")) return "MooTools";
        if (file.StartsWith("dojo")) return "Dojo";
        if (file.StartsWith("ext-all")) return "ExtJS (legacy bundle)";
        if (file.StartsWith("microsoftajax")) return "ASP.NET AJAX (MicrosoftAjax)";
        return file;
    }

    private static void Add(Dictionary<string, (bool Legacy, int Count, HashSet<string> Where)> frameworks, string name, bool legacy, string where)
    {
        if (frameworks.TryGetValue(name, out var existing))
        {
            existing.Where.Add(where);
            frameworks[name] = (existing.Legacy || legacy, existing.Count + 1, existing.Where);
        }
        else
        {
            frameworks[name] = (legacy, 1, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { where });
        }
    }

    private static bool IsVendorOrGenerated(string path)
    {
        var lower = path.ToLowerInvariant();
        return lower.Contains("/node_modules/") || lower.Contains("/bower_components/") || lower.Contains("/wwwroot/lib/") || lower.Contains("/scripts/lib/")
            || lower.Contains("/dist/") || lower.Contains("/vendor/") || lower.EndsWith(".min.js") || lower.EndsWith(".bundle.js") || lower.Contains("jquery") || lower.Contains("/typings/") || lower.EndsWith(".d.ts");
    }

    private static async Task<string?> ReadAsync(IArtifactReader workspace, string path, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = workspace.OpenRead(path);
            if (stream.CanSeek && stream.Length > MaxFileBytes)
            {
                return null;
            }

            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(cancellationToken);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string LineAt(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var end = text.IndexOf('\n', index);
        return text[start..(end < 0 ? text.Length : end)];
    }

    private static string Fingerprint(string snippet) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(snippet.Trim())))[..16];
}
