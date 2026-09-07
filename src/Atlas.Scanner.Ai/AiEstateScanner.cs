using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Atlas.Domain.Findings;
using Atlas.Domain.Rules;
using Atlas.Domain.Workspaces;
using Atlas.Scanner.Abstractions;
using Atlas.Scanner.Ai.Catalog;
using Atlas.Scanner.Ai.Manifests;
using Atlas.Scanner.Ai.Mcp;
using Atlas.Security.Redaction;
using Microsoft.Extensions.Logging;

namespace Atlas.Scanner.Ai;

/// <summary>
/// AI Estate (V0.5 of the governance module, the design notes): what AI does this repository use, declared in
/// manifests and visible in code — provider SDKs, agent frameworks, direct provider endpoints, model
/// references (including retired ones), MCP servers the repository's tools connect to, and, when the
/// organization keeps an allowlist, providers that are not approved. Everything is matched against a
/// versioned signature catalog; nothing is hardcoded here. Read-only, deterministic, no execution;
/// credential values found in tool configs are fingerprinted at the source and never stored.
/// </summary>
public sealed partial class AiEstateScanner(SignatureCatalog catalog, AiEstateOptions options, ILogger<AiEstateScanner> logger) : IScanner
{
    public static class RuleIds
    {
        public const string Inventory = "ai.inventory";
        public const string ProviderSdk = "ai.provider.sdk";
        public const string DirectEndpoint = "ai.provider.direct-endpoint";
        public const string RetiredModel = "ai.model.retired";
        public const string AgentFramework = "ai.agent.framework";
        public const string McpServer = "ai.mcp.server";
        public const string McpRemoteServer = "ai.mcp.remote-server";
        public const string McpSecretInConfig = "ai.mcp.secret-in-config";
        public const string LocalRuntime = "ai.local-runtime";
        public const string UnapprovedProvider = "ai.unapproved-provider";
    }

    public const string ScannerId = "ai.estate";
    private const string RulesVersion = "1.0.0";
    private const string Pt = "pt-BR";
    private const int MaxPerRule = 200;
    private const int MaxTextFiles = 40_000;
    private const long MaxTextBytes = 1_000_000;
    private const int MaxListInJson = 50;

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".vb", ".py", ".ipynb", ".java", ".kt", ".kts", ".scala", ".groovy", ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs", ".go", ".rb", ".php",
    };

    private static readonly HashSet<string> ConfigExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".yaml", ".yml", ".toml", ".ini", ".properties", ".env", ".config", ".tf", ".bicep", ".xml", ".txt", ".md", ".cfg", ".conf", ".ps1", ".sh", ".bat", ".cmd",
    };

    private static IReadOnlyDictionary<string, RuleLocalization> Loc(RuleLocalization pt) => new Dictionary<string, RuleLocalization> { [Pt] = pt };

    public ScannerDescriptor Descriptor { get; } = new(
        Id: ScannerId,
        Name: "AI Estate Scanner",
        Version: "0.1.0",
        Category: FindingCategory.Security,
        Capabilities: ["ai-sdk-inventory", "provider-endpoints", "model-references", "agent-frameworks", "mcp-discovery", "provider-allowlist", "signature-catalog"]);

    public IReadOnlyList<RuleSpec> Rules { get; } =
    [
        new(RuleIds.Inventory, RulesVersion, FindingCategory.Architecture, Severity.Informational,
            "AI estate of the repository", "Every AI provider, SDK, agent framework, model, MCP server and local runtime the repository declares or calls, matched against the versioned signature catalog. The AI Estate section of the report is built from this record.",
            null,
            Loc(new("AI estate do repositório", "Todos os provedores de IA, SDKs, frameworks de agente, modelos, servidores MCP e runtimes locais que o repositório declara ou chama, casados com o catálogo de assinaturas versionado. A seção AI Estate do relatório é montada a partir deste registro.", null,
                "AI estate do repositório", "{providers} provedor(es), {sdks} SDK(s), {frameworks} framework(s) de agente, {mcpServers} servidor(es) MCP, {models} modelo(s) referenciado(s); catálogo {catalogVersion}."))),
        new(RuleIds.ProviderSdk, RulesVersion, FindingCategory.Dependencies, Severity.Informational,
            "AI provider SDK in use", "A package that talks to an AI provider (or a vector store / gateway) is declared in a manifest or imported in code. Inventory evidence: where the AI dependency enters the system.",
            null,
            Loc(new("SDK de provedor de IA em uso", "Um pacote que fala com um provedor de IA (ou vector store / gateway) está declarado em um manifesto ou importado no código. Evidência de inventário: por onde a dependência de IA entra no sistema.", null,
                "{package} ({ecosystem})", "{package} {version} declarado em {manifest}; provedor: {provider}; categoria: {category}."))),
        new(RuleIds.DirectEndpoint, RulesVersion, FindingCategory.Architecture, Severity.Low,
            "Direct call to an AI provider endpoint", "The provider's API host appears literally in code or configuration: calls bypass any gateway, so cost attribution, rate limits, logging and data-flow controls depend on this repository alone.",
            "Route provider traffic through one configured base URL (gateway or Azure/AWS-hosted endpoint) so cost, quotas and audit are centralized; keep the host in configuration, not in code.",
            Loc(new("Chamada direta a endpoint de provedor de IA", "O host da API do provedor aparece literalmente em código ou configuração: as chamadas não passam por gateway, então atribuição de custo, limites, logging e controle de fluxo de dados dependem só deste repositório.",
                "Roteie o tráfego do provedor por uma base URL configurada (gateway ou endpoint hospedado no Azure/AWS) para centralizar custo, cotas e auditoria; mantenha o host em configuração, não em código.",
                "Endpoint {provider} em {fileName}", "{host} referenciado em {file} (linha {line})."))),
        new(RuleIds.RetiredModel, RulesVersion, FindingCategory.Modernization, Severity.Medium,
            "Reference to a retired AI model", "The code names a model the provider has retired (or announced a shutdown date for). Calls fail or are silently redirected, and the replacement usually changes cost and behaviour — a migration item, not a one-line rename.",
            "Move to the replacement model named in the finding, re-run the evaluation set that guards this feature, and pin the model id in configuration.",
            Loc(new("Referência a modelo de IA descontinuado", "O código nomeia um modelo que o provedor descontinuou (ou anunciou data de desligamento). As chamadas falham ou são redirecionadas silenciosamente, e a substituição costuma mudar custo e comportamento — item de migração, não rename de uma linha.",
                "Migre para o modelo substituto indicado no finding, reexecute o conjunto de avaliação que protege esta funcionalidade e fixe o id do modelo em configuração.",
                "Modelo descontinuado: {model}", "{model} ({provider}) descontinuado em {retiredOn}; substituto sugerido: {replacement}. Referenciado em {file}."))),
        new(RuleIds.AgentFramework, RulesVersion, FindingCategory.Architecture, Severity.Informational,
            "Agent framework in use", "An agent/orchestration framework (Semantic Kernel, LangChain, AutoGen, CrewAI, Spring AI…) is declared. Agents call tools and act on data: they deserve an explicit owner, a tool allowlist and evaluation before production.",
            null,
            Loc(new("Framework de agente em uso", "Um framework de agentes/orquestração (Semantic Kernel, LangChain, AutoGen, CrewAI, Spring AI…) está declarado. Agentes chamam ferramentas e agem sobre dados: merecem dono explícito, allowlist de ferramentas e avaliação antes de produção.", null,
                "Framework de agente: {framework}", "{framework} via {package} declarado em {manifest}."))),
        new(RuleIds.McpServer, RulesVersion, FindingCategory.Architecture, Severity.Low,
            "MCP server configured in the repository", "A Model Context Protocol server is configured for the coding assistants used on this repository. Each server is a tool an AI can invoke: its capability class (read / write / execute) is what governance needs to know.",
            "Keep only the servers the team needs, prefer read-only ones, and pin versions (npx/uvx without a version runs whatever is published today).",
            Loc(new("Servidor MCP configurado no repositório", "Um servidor Model Context Protocol está configurado para os assistentes de código usados neste repositório. Cada servidor é uma ferramenta que a IA pode invocar: sua classe de capacidade (leitura / escrita / execução) é o que a governança precisa saber.",
                "Mantenha só os servidores de que o time precisa, prefira os somente-leitura e fixe versões (npx/uvx sem versão executa o que estiver publicado hoje).",
                "MCP {server} ({capability})", "{server} em {config}: transporte {transport}, capacidade {capability}{knownServer}."))),
        new(RuleIds.McpRemoteServer, RulesVersion, FindingCategory.Security, Severity.Medium,
            "Remote MCP server", "An MCP server configured here runs on a remote host: prompts, tool arguments and results cross the network to a third party. Confirm the data-processing agreement and the authentication in use.",
            "Verify who operates the host, what data the tools receive, and that authentication is OAuth/short-lived tokens rather than a static key in the config.",
            Loc(new("Servidor MCP remoto", "Um servidor MCP configurado aqui roda em host remoto: prompts, argumentos e resultados de ferramentas atravessam a rede até um terceiro. Confirme o acordo de tratamento de dados e a autenticação em uso.",
                "Verifique quem opera o host, que dados as ferramentas recebem e se a autenticação é OAuth/tokens curtos em vez de chave estática na configuração.",
                "MCP remoto: {server} → {host}", "{server} em {config} aponta para {host} (transporte {transport})."))),
        new(RuleIds.McpSecretInConfig, RulesVersion, FindingCategory.Secrets, Severity.High,
            "Credential embedded in MCP configuration", "An MCP server definition carries a literal token or key (env value, header or argument) committed to the repository. The value is fingerprinted, never stored.",
            "Remove and rotate the credential; reference it through an environment variable or the assistant's secret store.",
            Loc(new("Credencial embutida em configuração MCP", "Uma definição de servidor MCP carrega um token ou chave literal (valor de env, header ou argumento) commitado no repositório. O valor é fingerprintado, nunca armazenado.",
                "Remova e rotacione a credencial; referencie-a por variável de ambiente ou pelo cofre de segredos do assistente.",
                "Credencial em MCP {server}", "{location} do servidor {server} em {config} contém um valor com aparência de credencial (fingerprint {fingerprint}, prévia {preview})."))),
        new(RuleIds.LocalRuntime, RulesVersion, FindingCategory.Architecture, Severity.Informational,
            "Local AI runtime endpoint", "The code targets a locally hosted model runtime (Ollama, LM Studio, vLLM). Data stays on-premises, but the model, its version and its host are outside any provider contract — inventory them.",
            null,
            Loc(new("Endpoint de runtime de IA local", "O código aponta para um runtime de modelo hospedado localmente (Ollama, LM Studio, vLLM). Os dados ficam on-premises, mas modelo, versão e host estão fora de qualquer contrato com provedor — inventarie-os.", null,
                "Runtime local {provider} em {fileName}", "{host} referenciado em {file} (linha {line})."))),
        new(RuleIds.UnapprovedProvider, RulesVersion, FindingCategory.Security, Severity.High,
            "AI provider not on the approved list", "The repository uses an external AI provider that is not in the organization's approved-provider list (Atlas:AiEstate:ApprovedProviders). Data may be leaving to a vendor without a contract, DPA or security review.",
            "Either approve the provider (contract, DPA, data classification) and add it to the allowlist, or migrate the integration to an approved provider.",
            Loc(new("Provedor de IA fora da lista aprovada", "O repositório usa um provedor externo de IA que não está na lista de provedores aprovados da organização (Atlas:AiEstate:ApprovedProviders). Dados podem estar saindo para um fornecedor sem contrato, DPA ou revisão de segurança.",
                "Aprove o provedor (contrato, DPA, classificação de dados) e inclua-o na allowlist, ou migre a integração para um provedor aprovado.",
                "Provedor não aprovado: {provider}", "{provider} detectado via {evidence}; provedores aprovados: {approved}."))),
    ];

    public async Task<ScanResult> ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var estate = new Estate(catalog);

        // 1. Declared packages: NuGet from the language adapters, everything else from manifests.
        foreach (var fact in PackageManifests.FromProjects(context.Languages))
        {
            estate.AddPackage(fact);
        }

        var allFiles = context.Workspace.SourceFiles("*").Select(p => p.Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal).ToList();
        foreach (var (pattern, parse) in PackageManifests.Manifests)
        {
            foreach (var path in allFiles.Where(p => MatchesManifest(p, pattern)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = await ReadAsync(context.Workspace, path, cancellationToken);
                if (text is not null)
                {
                    foreach (var fact in parse(path, text))
                    {
                        estate.AddPackage(fact);
                    }
                }
            }
        }

        // 2. Code and configuration: endpoints, models, env vars, imports.
        var textFiles = allFiles.Where(IsTextCandidate).Take(MaxTextFiles).ToList();
        foreach (var path in textFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await ReadAsync(context.Workspace, path, cancellationToken);
            if (text is null)
            {
                continue;
            }

            ScanText(estate, path, text);
        }

        // 3. MCP client configurations.
        var hmacKey = HmacFingerprint.KeyFromBase64(options.HmacKeyBase64);
        foreach (var path in allFiles.Where(p => catalog.Document.Mcp.ConfigFiles.Any(name => p.Equals(name, StringComparison.OrdinalIgnoreCase) || p.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await ReadAsync(context.Workspace, path, cancellationToken);
            if (text is not null)
            {
                estate.McpServers.AddRange(McpConfigReader.Read(path, text, catalog, hmacKey));
            }
        }

        if (estate.IsEmpty)
        {
            return ScanResult.Success();
        }

        Emit(context, estate);
        logger.LogInformation("AI estate: {Providers} provider(s), {Packages} package(s), {Mcp} MCP server(s) in {Files} text file(s); catalog {Version} ({Hash}).",
            estate.Providers.Count, estate.Packages.Count, estate.McpServers.Count, textFiles.Count, catalog.Version, catalog.Hash[..12]);
        return ScanResult.Success();
    }

    // ---- text scanning ----

    [GeneratedRegex(@"^\s*(?:global\s+)?using\s+(?:static\s+)?([A-Za-z_][\w.]*)\s*;", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex CSharpUsing();

    [GeneratedRegex(@"^\s*Imports\s+([A-Za-z_][\w.]*)", RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex VbImports();

    [GeneratedRegex(@"^\s*(?:from\s+([\w.]+)\s+import\b|import\s+([\w.]+))", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PythonImport();

    [GeneratedRegex(@"(?:from\s+|require\(\s*|import\s*\(\s*|^\s*import\s+)['""]([^'""\n]+)['""]", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex JsImport();

    [GeneratedRegex(@"^\s*import\s+(?:static\s+)?([a-z][\w.]*)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex JavaImport();

    private void ScanText(Estate estate, string path, string text)
    {
        foreach (var (signature, regex) in catalog.Endpoints)
        {
            var m = SafeMatch(regex, text);
            if (m is { Success: true })
            {
                estate.AddEndpoint(signature, path, m.Value, LineOf(text, m.Index));
            }
        }

        foreach (var (signature, regex) in catalog.Models)
        {
            var m = SafeMatch(regex, text);
            if (m is { Success: true })
            {
                estate.AddModel(signature, path, m.Value, LineOf(text, m.Index));
            }
        }

        if (catalog.EnvVarPattern is not null)
        {
            foreach (var name in SafeMatches(catalog.EnvVarPattern, text).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal))
            {
                if (catalog.EnvVars.TryGetValue(name, out var envVar))
                {
                    estate.AddEnvVar(envVar, path);
                }
            }
        }

        var extension = Path.GetExtension(path);
        var (ecosystem, regex2) = extension.ToLowerInvariant() switch
        {
            ".cs" => (PackageManifests.NuGet, CSharpUsing()),
            ".vb" => (PackageManifests.NuGet, VbImports()),
            ".py" or ".ipynb" => (PackageManifests.PyPi, PythonImport()),
            ".js" or ".jsx" or ".ts" or ".tsx" or ".mjs" or ".cjs" => (PackageManifests.Npm, JsImport()),
            ".java" or ".kt" or ".kts" or ".scala" or ".groovy" => (PackageManifests.Maven, JavaImport()),
            _ => (null, null),
        };
        if (ecosystem is null || regex2 is null)
        {
            return;
        }

        foreach (var m in SafeMatches(regex2, text))
        {
            var import = m.Groups.Cast<Group>().Skip(1).Select(g => g.Value).FirstOrDefault(v => v.Length > 0);
            if (import is null)
            {
                continue;
            }

            if (ecosystem == PackageManifests.Npm)
            {
                import = NpmPackageOf(import);
                if (import is null)
                {
                    continue;
                }
            }

            var signature = catalog.MatchImport(ecosystem, import);
            if (signature is not null)
            {
                estate.AddImport(signature, path);
            }
        }
    }

    /// <summary>Import specifier → package name: "@scope/pkg/sub" → "@scope/pkg", "pkg/sub" → "pkg"; relative paths → null.</summary>
    internal static string? NpmPackageOf(string specifier)
    {
        if (specifier.StartsWith('.') || specifier.StartsWith('/') || specifier.StartsWith("node:", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = specifier.Split('/');
        return specifier.StartsWith('@') ? (parts.Length >= 2 ? parts[0] + "/" + parts[1] : null) : parts[0];
    }

    private static Match? SafeMatch(Regex regex, string text)
    {
        try
        {
            return regex.Match(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static IEnumerable<Match> SafeMatches(Regex regex, string text)
    {
        try
        {
            return regex.Matches(text).ToList();
        }
        catch (RegexMatchTimeoutException)
        {
            return [];
        }
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static bool MatchesManifest(string path, string pattern)
    {
        var name = Path.GetFileName(path);
        if (pattern == "requirements*.txt")
        {
            return name.StartsWith("requirements", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        }

        return name.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTextCandidate(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(name);
        if (extension.Length == 0)
        {
            return name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) || name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase);
        }

        if (name.StartsWith(".env", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || name.Equals("package-lock.json", StringComparison.OrdinalIgnoreCase) || name.Equals("yarn.lock", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pnpm-lock.yaml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return SourceExtensions.Contains(extension) || ConfigExtensions.Contains(extension);
    }

    private static async Task<string?> ReadAsync(IArtifactReader workspace, string path, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = workspace.OpenRead(path);
            if (stream.CanSeek && stream.Length > MaxTextBytes)
            {
                return null;
            }

            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[Math.Min(MaxTextBytes, 1 << 16)];
            var sb = new System.Text.StringBuilder();
            int read;
            while ((read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
            {
                sb.Append(buffer, 0, read);
                if (sb.Length > MaxTextBytes)
                {
                    return null; // a file this large is generated or vendored, not hand-written integration code
                }
            }

            var text = sb.ToString();
            return text.Contains('\0') ? null : text;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    // ---- emission ----

    private void Emit(ScanContext context, Estate estate)
    {
        var sink = context.Findings;
        var approved = ResolveAllowlist(context);

        // Provider SDKs and agent frameworks: one finding per (package, manifest); import-only evidence gets one per package.
        var sdkCount = 0;
        var frameworkCount = 0;
        foreach (var hit in estate.Packages.Values.OrderBy(h => h.Signature.Ecosystem, StringComparer.Ordinal).ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase))
        {
            var provider = catalog.Provider(hit.Signature.Provider);
            var manifests = hit.Manifests.Count > 0 ? hit.Manifests.OrderBy(m => m.Key, StringComparer.Ordinal).ToList() : [new KeyValuePair<string, string?>(hit.ImportFiles.Min(StringComparer.Ordinal)!, null)];
            foreach (var (manifest, version) in manifests)
            {
                if (hit.Signature.Category == "agent-framework")
                {
                    if (frameworkCount++ < MaxPerRule)
                    {
                        sink.Emit(new FindingCandidate(
                            RuleIds.AgentFramework, Severity.Informational, ConfidenceLevel.High,
                            Title: $"Agent framework: {hit.Signature.Framework}",
                            Message: $"{hit.Signature.Framework} via {hit.Name} declared in {manifest}.",
                            Evidence: new EvidenceCandidate(FilePath: manifest, Symbol: $"{hit.Signature.Ecosystem}:{hit.Name}"),
                            Data: new Dictionary<string, string>
                            {
                                ["framework"] = hit.Signature.Framework!, ["package"] = hit.Name, ["ecosystem"] = hit.Signature.Ecosystem, ["manifest"] = manifest,
                                ["provider"] = provider?.Id ?? "—", ["codeFiles"] = hit.ImportFiles.Count.ToString(CultureInfo.InvariantCulture), ["catalogHash"] = catalog.Hash[..16],
                            }));
                    }
                }
                else if (sdkCount++ < MaxPerRule)
                {
                    var source = hit.Manifests.Count > 0 ? "manifest" : "import";
                    sink.Emit(new FindingCandidate(
                        RuleIds.ProviderSdk, Severity.Informational, hit.Manifests.Count > 0 ? ConfidenceLevel.High : ConfidenceLevel.Medium,
                        Title: $"{hit.Name} ({hit.Signature.Ecosystem})",
                        Message: source == "manifest"
                            ? $"{hit.Name} {version ?? string.Empty} declared in {manifest}; provider: {provider?.Name ?? "—"}; category: {hit.Signature.Category}."
                            : $"{hit.Name} imported in {hit.ImportFiles.Count} file(s) (no manifest declares it); provider: {provider?.Name ?? "—"}; category: {hit.Signature.Category}.",
                        Evidence: new EvidenceCandidate(FilePath: manifest, Symbol: $"{hit.Signature.Ecosystem}:{hit.Name}"),
                        Data: new Dictionary<string, string>
                        {
                            ["package"] = hit.Name, ["ecosystem"] = hit.Signature.Ecosystem, ["version"] = version ?? "—", ["manifest"] = manifest, ["source"] = source,
                            ["provider"] = provider?.Name ?? "—", ["providerId"] = provider?.Id ?? "—", ["providerKind"] = provider?.Kind ?? "—", ["category"] = hit.Signature.Category,
                            ["codeFiles"] = hit.ImportFiles.Count.ToString(CultureInfo.InvariantCulture), ["catalogHash"] = catalog.Hash[..16],
                        }));
                }
            }
        }

        // Endpoints: external providers → direct-endpoint; local runtimes → local-runtime.
        var endpointCount = 0;
        var localCount = 0;
        foreach (var hit in estate.Endpoints.OrderBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.Signature.Id, StringComparer.Ordinal))
        {
            var provider = catalog.Provider(hit.Signature.Provider)!;
            var local = provider.Kind is "local-runtime";
            if ((local ? localCount++ : endpointCount++) >= MaxPerRule)
            {
                continue;
            }

            sink.Emit(new FindingCandidate(
                local ? RuleIds.LocalRuntime : RuleIds.DirectEndpoint,
                local ? Severity.Informational : Severity.Low,
                ConfidenceLevel.High,
                Title: local ? $"Local runtime {provider.Name} in {Path.GetFileName(hit.Path)}" : $"{provider.Name} endpoint in {Path.GetFileName(hit.Path)}",
                Message: $"{hit.Host} referenced in {hit.Path} (line {hit.Line}).",
                Evidence: new EvidenceCandidate(FilePath: hit.Path, LineStart: hit.Line, Symbol: hit.Signature.Id),
                Data: new Dictionary<string, string>
                {
                    ["provider"] = provider.Name, ["providerId"] = provider.Id, ["host"] = hit.Host, ["file"] = hit.Path, ["fileName"] = Path.GetFileName(hit.Path),
                    ["line"] = hit.Line.ToString(CultureInfo.InvariantCulture), ["signature"] = hit.Signature.Id, ["catalogHash"] = catalog.Hash[..16],
                }));
        }

        // Retired models: one per (file, signature).
        var retiredCount = 0;
        foreach (var hit in estate.Models.Where(m => m.Signature.RetiredOn is not null).OrderBy(m => m.Path, StringComparer.Ordinal).ThenBy(m => m.Signature.Id, StringComparer.Ordinal))
        {
            if (retiredCount++ >= MaxPerRule)
            {
                break;
            }

            var provider = catalog.Provider(hit.Signature.Provider)!;
            sink.Emit(new FindingCandidate(
                RuleIds.RetiredModel, Severity.Medium, ConfidenceLevel.Medium,
                Title: $"Retired model: {hit.Model}",
                Message: $"{hit.Model} ({provider.Name}) was retired on {hit.Signature.RetiredOn}; suggested replacement: {hit.Signature.Replacement ?? "see provider deprecations"}. Referenced in {hit.Path}.",
                Evidence: new EvidenceCandidate(FilePath: hit.Path, LineStart: hit.Line, Symbol: hit.Signature.Id),
                Remediation: hit.Signature.Replacement is null ? null : $"Migrate to {hit.Signature.Replacement} and re-run the feature's evaluation set.",
                Data: new Dictionary<string, string>
                {
                    ["model"] = hit.Model, ["provider"] = provider.Name, ["providerId"] = provider.Id, ["retiredOn"] = hit.Signature.RetiredOn!,
                    ["replacement"] = hit.Signature.Replacement ?? "—", ["file"] = hit.Path, ["line"] = hit.Line.ToString(CultureInfo.InvariantCulture), ["catalogHash"] = catalog.Hash[..16],
                }));
        }

        // MCP servers.
        var mcpCount = 0;
        var remoteCount = 0;
        var secretCount = 0;
        foreach (var server in estate.McpServers.OrderBy(s => s.ConfigPath, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal))
        {
            if (mcpCount++ < MaxPerRule)
            {
                sink.Emit(new FindingCandidate(
                    RuleIds.McpServer, Severity.Low, ConfidenceLevel.High,
                    Title: $"MCP {server.Name} ({server.Capability})",
                    Message: $"{server.Name} in {server.ConfigPath}: transport {server.Transport}, capability {server.Capability}{(server.KnownServerName is null ? string.Empty : $", recognized as {server.KnownServerName}")}.",
                    Evidence: new EvidenceCandidate(FilePath: server.ConfigPath, Symbol: $"mcp:{server.Name}"),
                    Data: new Dictionary<string, string>
                    {
                        ["server"] = server.Name, ["config"] = server.ConfigPath, ["transport"] = server.Transport, ["capability"] = server.Capability,
                        ["knownServer"] = server.KnownServerName is null ? string.Empty : $", reconhecido como {server.KnownServerName}", ["knownServerName"] = server.KnownServerName ?? "—",
                        ["command"] = server.Command ?? "—", ["host"] = server.Host ?? "—", ["remote"] = server.IsRemote ? "true" : "false",
                        ["envKeys"] = string.Join(", ", server.EnvKeys), ["catalogHash"] = catalog.Hash[..16],
                    }));
            }

            if (server.IsRemote && remoteCount++ < MaxPerRule)
            {
                sink.Emit(new FindingCandidate(
                    RuleIds.McpRemoteServer, Severity.Medium, ConfidenceLevel.High,
                    Title: $"Remote MCP: {server.Name} → {server.Host}",
                    Message: $"{server.Name} in {server.ConfigPath} points to {server.Host} (transport {server.Transport}).",
                    Evidence: new EvidenceCandidate(FilePath: server.ConfigPath, Symbol: $"mcp-remote:{server.Name}"),
                    Data: new Dictionary<string, string>
                    {
                        ["server"] = server.Name, ["config"] = server.ConfigPath, ["transport"] = server.Transport, ["host"] = server.Host!, ["capability"] = server.Capability, ["catalogHash"] = catalog.Hash[..16],
                    }));
            }

            foreach (var secret in server.Secrets)
            {
                if (secretCount++ >= MaxPerRule)
                {
                    break;
                }

                sink.Emit(new FindingCandidate(
                    RuleIds.McpSecretInConfig, Severity.High, ConfidenceLevel.Medium,
                    Title: $"Credential in MCP {server.Name}",
                    Message: $"{secret.Location} of server {server.Name} in {server.ConfigPath} holds a credential-looking value (fingerprint {secret.Fingerprint}, preview {secret.Preview}).",
                    Evidence: new EvidenceCandidate(FilePath: server.ConfigPath, Symbol: $"mcp-secret:{server.Name}:{secret.Location}", SnippetHash: secret.Fingerprint),
                    Data: new Dictionary<string, string>
                    {
                        ["server"] = server.Name, ["config"] = server.ConfigPath, ["location"] = secret.Location, ["fingerprint"] = secret.Fingerprint, ["preview"] = secret.Preview, ["catalogHash"] = catalog.Hash[..16],
                    }));
            }
        }

        // Allowlist: every external provider with evidence that is not approved.
        var unapproved = new List<string>();
        if (approved is not null)
        {
            foreach (var (id, evidence) in estate.Providers.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var provider = catalog.Provider(id)!;
                if (provider.Kind != "external-api" || approved.Contains(id))
                {
                    continue;
                }

                unapproved.Add(id);
                var summary = evidence.Summary();
                sink.Emit(new FindingCandidate(
                    RuleIds.UnapprovedProvider, Severity.High, evidence.Packages.Count > 0 || evidence.EndpointFiles.Count > 0 ? ConfidenceLevel.High : ConfidenceLevel.Medium,
                    Title: $"Unapproved provider: {provider.Name}",
                    Message: $"{provider.Name} detected via {summary}; approved providers: {string.Join(", ", approved.OrderBy(a => a, StringComparer.Ordinal))}.",
                    Evidence: new EvidenceCandidate(FilePath: evidence.FirstFile, Symbol: $"provider:{id}"),
                    Data: new Dictionary<string, string>
                    {
                        ["provider"] = provider.Name, ["providerId"] = id, ["evidence"] = summary, ["approved"] = string.Join(", ", approved.OrderBy(a => a, StringComparer.Ordinal)), ["catalogHash"] = catalog.Hash[..16],
                    }));
            }
        }

        // The inventory record the report is built from.
        var frameworks = estate.Packages.Values.Where(h => h.Signature.Category == "agent-framework").Select(h => h.Signature.Framework!).Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var models = estate.Models.Select(m => m.Model).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        var inventoryData = new Dictionary<string, string>
        {
            ["providers"] = estate.Providers.Count.ToString(CultureInfo.InvariantCulture),
            ["providerList"] = string.Join(", ", estate.Providers.Keys.OrderBy(k => k, StringComparer.Ordinal)),
            ["sdks"] = estate.Packages.Values.Count(h => h.Signature.Category != "agent-framework").ToString(CultureInfo.InvariantCulture),
            ["frameworks"] = frameworks.Count.ToString(CultureInfo.InvariantCulture),
            ["mcpServers"] = estate.McpServers.Count.ToString(CultureInfo.InvariantCulture),
            ["models"] = models.Count.ToString(CultureInfo.InvariantCulture),
            ["retiredModels"] = estate.Models.Where(m => m.Signature.RetiredOn is not null).Select(m => m.Model).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString(CultureInfo.InvariantCulture),
            ["endpoints"] = estate.Endpoints.Count.ToString(CultureInfo.InvariantCulture),
            ["unapproved"] = unapproved.Count.ToString(CultureInfo.InvariantCulture),
            ["allowlistConfigured"] = approved is null ? "false" : "true",
            ["catalogVersion"] = catalog.Version,
            ["catalogHash"] = catalog.Hash[..16],
            ["estateJson"] = BuildEstateJson(estate, frameworks, models, approved, unapproved),
        };
        sink.Emit(new FindingCandidate(
            RuleIds.Inventory, Severity.Informational, ConfidenceLevel.High,
            Title: "AI estate of the repository",
            Message: $"{inventoryData["providers"]} provider(s), {inventoryData["sdks"]} SDK(s), {frameworks.Count} agent framework(s), {estate.McpServers.Count} MCP server(s), {models.Count} model(s) referenced; catalog {catalog.Version}.",
            Evidence: new EvidenceCandidate(FilePath: estate.FirstFile, Symbol: "ai-estate"),
            Data: inventoryData));
    }

    /// <summary>The tenant's UI-managed allowlist travels as a scan setting and wins; Atlas:AiEstate:ApprovedProviders is the deployment fallback.</summary>
    internal HashSet<string>? ResolveAllowlist(ScanContext context)
    {
        IEnumerable<string> source = context.Settings.TryGetValue(ScanSettingKeys.AiApprovedProviders, out var setting) && !string.IsNullOrWhiteSpace(setting)
            ? setting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : options.HasAllowlist ? options.ApprovedProviders : [];
        var set = source.Select(p => p.Trim().ToLowerInvariant()).Where(p => p.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return set.Count == 0 ? null : set;
    }

    private string BuildEstateJson(Estate estate, List<string> frameworks, List<string> models, HashSet<string>? approved, List<string> unapproved)
    {
        var doc = new
        {
            catalog = new { version = catalog.Version, hash = catalog.Hash, source = catalog.Source },
            providers = estate.Providers.OrderBy(p => p.Key, StringComparer.Ordinal).Take(MaxListInJson).Select(p =>
            {
                var provider = catalog.Provider(p.Key)!;
                return new
                {
                    id = provider.Id, name = provider.Name, kind = provider.Kind,
                    packages = p.Value.Packages.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(MaxListInJson).ToList(),
                    endpointFiles = p.Value.EndpointFiles.Count,
                    envVars = p.Value.EnvVars.OrderBy(x => x, StringComparer.Ordinal).Take(MaxListInJson).ToList(),
                    models = p.Value.Models.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(MaxListInJson).ToList(),
                    codeFiles = p.Value.CodeFiles.Count,
                    approved = approved is null ? (bool?)null : approved.Contains(provider.Id) || provider.Kind != "external-api",
                };
            }).ToList(),
            frameworks = frameworks.Take(MaxListInJson).Select(f => new
            {
                name = f,
                packages = estate.Packages.Values.Where(h => h.Signature.Framework == f).Select(h => $"{h.Signature.Ecosystem}:{h.Name}").OrderBy(x => x, StringComparer.Ordinal).Take(MaxListInJson).ToList(),
            }).ToList(),
            mcp = estate.McpServers.OrderBy(s => s.ConfigPath, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal).Take(MaxListInJson).Select(s => new
            {
                config = s.ConfigPath, name = s.Name, transport = s.Transport, capability = s.Capability, remote = s.IsRemote, host = s.Host, command = s.Command, known = s.KnownServerName, secrets = s.Secrets.Count,
            }).ToList(),
            models = new
            {
                active = models.Where(m => !estate.Models.Any(x => x.Model.Equals(m, StringComparison.OrdinalIgnoreCase) && x.Signature.RetiredOn is not null)).Take(MaxListInJson).ToList(),
                retired = estate.Models.Where(m => m.Signature.RetiredOn is not null)
                    .GroupBy(m => m.Model, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(MaxListInJson)
                    .Select(g => new { model = g.Key, retiredOn = g.First().Signature.RetiredOn, replacement = g.First().Signature.Replacement, files = g.Select(x => x.Path).Distinct().Count() })
                    .ToList(),
            },
            vectorStores = estate.Providers.Keys.Where(k => catalog.Provider(k)!.Kind == "vector-store").OrderBy(k => k, StringComparer.Ordinal).ToList(),
            localRuntimes = estate.Providers.Keys.Where(k => catalog.Provider(k)!.Kind is "local-runtime" or "local-inference").OrderBy(k => k, StringComparer.Ordinal).ToList(),
            gateways = estate.Providers.Keys.Where(k => catalog.Provider(k)!.Kind is "gateway").OrderBy(k => k, StringComparer.Ordinal).ToList(),
            allowlist = new { configured = approved is not null, approved = approved?.OrderBy(a => a, StringComparer.Ordinal).ToList(), unapproved },
            secretsInMcp = estate.McpServers.Sum(s => s.Secrets.Count),
        };
        return JsonSerializer.Serialize(doc);
    }

    // ---- accumulation ----

    private sealed class PackageHit(PackageSignature signature, string name)
    {
        public PackageSignature Signature { get; } = signature;

        public string Name { get; } = name;

        /// <summary>manifest path → declared version.</summary>
        public Dictionary<string, string?> Manifests { get; } = new(StringComparer.Ordinal);

        public HashSet<string> ImportFiles { get; } = new(StringComparer.Ordinal);
    }

    private sealed record EndpointHit(PatternSignature Signature, string Path, string Host, int Line);

    private sealed record ModelHit(PatternSignature Signature, string Path, string Model, int Line);

    private sealed class ProviderEvidence
    {
        public HashSet<string> Packages { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> EndpointFiles { get; } = new(StringComparer.Ordinal);

        public HashSet<string> EnvVars { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Models { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> CodeFiles { get; } = new(StringComparer.Ordinal);

        public string? FirstFile { get; set; }

        public string Summary()
        {
            var parts = new List<string>();
            if (Packages.Count > 0)
            {
                parts.Add($"{Packages.Count} package(s)");
            }

            if (EndpointFiles.Count > 0)
            {
                parts.Add($"endpoint in {EndpointFiles.Count} file(s)");
            }

            if (Models.Count > 0)
            {
                parts.Add($"{Models.Count} model reference(s)");
            }

            if (EnvVars.Count > 0)
            {
                parts.Add($"env var(s) {string.Join("/", EnvVars.OrderBy(v => v, StringComparer.Ordinal).Take(3))}");
            }

            return parts.Count == 0 ? "code imports" : string.Join(", ", parts);
        }
    }

    private sealed class Estate(SignatureCatalog catalog)
    {
        public Dictionary<string, PackageHit> Packages { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<EndpointHit> Endpoints { get; } = [];

        public List<ModelHit> Models { get; } = [];

        public List<McpServerFact> McpServers { get; } = [];

        public Dictionary<string, ProviderEvidence> Providers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? FirstFile { get; private set; }

        public bool IsEmpty => Packages.Count == 0 && Endpoints.Count == 0 && Models.Count == 0 && McpServers.Count == 0 && Providers.Count == 0;

        public void AddPackage(PackageFact fact)
        {
            var signature = catalog.MatchPackage(fact.Ecosystem, fact.Name);
            if (signature is null)
            {
                return;
            }

            var hit = Hit(signature, fact.Name);
            hit.Manifests.TryAdd(fact.ManifestPath, fact.Version);
            Touch(fact.ManifestPath);
            if (signature.Provider is not null)
            {
                var evidence = Evidence(signature.Provider, fact.ManifestPath);
                evidence.Packages.Add($"{fact.Ecosystem}:{fact.Name}");
            }
        }

        public void AddImport(PackageSignature signature, string path)
        {
            var hit = Hit(signature, signature.Prefix ? signature.Name.TrimEnd(':', '-', '/') : signature.Name);
            hit.ImportFiles.Add(path);
            Touch(path);
            if (signature.Provider is not null)
            {
                var evidence = Evidence(signature.Provider, path);
                evidence.CodeFiles.Add(path);
                evidence.Packages.Add($"{signature.Ecosystem}:{hit.Name}");
            }
        }

        public void AddEndpoint(PatternSignature signature, string path, string host, int line)
        {
            Endpoints.Add(new EndpointHit(signature, path, host, line));
            Touch(path);
            Evidence(signature.Provider!, path).EndpointFiles.Add(path);
        }

        public void AddModel(PatternSignature signature, string path, string model, int line)
        {
            Models.Add(new ModelHit(signature, path, model, line));
            Touch(path);
            Evidence(signature.Provider!, path).Models.Add(model);
        }

        public void AddEnvVar(EnvVarSignature envVar, string path)
        {
            Touch(path);
            Evidence(envVar.Provider, path).EnvVars.Add(envVar.Name);
        }

        private PackageHit Hit(PackageSignature signature, string name)
        {
            var key = $"{signature.Ecosystem}:{name}";
            if (!Packages.TryGetValue(key, out var hit))
            {
                hit = new PackageHit(signature, name);
                Packages[key] = hit;
            }

            return hit;
        }

        private ProviderEvidence Evidence(string providerId, string path)
        {
            if (!Providers.TryGetValue(providerId, out var evidence))
            {
                evidence = new ProviderEvidence();
                Providers[providerId] = evidence;
            }

            evidence.FirstFile ??= path;
            return evidence;
        }

        private void Touch(string path) => FirstFile ??= path;
    }
}
