using System.Xml.Linq;
using Atlas.Domain.Findings;
using Atlas.Domain.Rules;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;

namespace Atlas.Scanner.Security;

/// <summary>
/// Security pattern scanner (.3): judges language-neutral pattern
/// facts produced by language adapters and checks configuration files as data.
/// Findings are candidates with severity + confidence — never a claim of
/// exploitability. Identity = rule + file + enclosing member.
/// Rules are bilingual (EN canonical + PT-BR templates over structured data).
/// </summary>
public sealed class SecurityScanner : IScanner
{
    public static class ConfigRuleIds
    {
        public const string DebugEnabled = "sec.config.debug-enabled";
        public const string CustomErrorsOff = "sec.config.custom-errors-off";
        public const string TraceEnabled = "sec.config.trace-enabled";
        public const string CookiesNotHttpOnly = "sec.config.cookies-not-httponly";
    }

    private const string RulesVersion = "1.0.0";
    private const string Pt = "pt-BR";
    private const string PatternMessagePt = "{detail} em {symbol} (linha {line}).";

    private sealed record PatternRule(Severity Severity, ConfidenceLevel Confidence, string Title, string Description, string Remediation, RuleLocalization PtBr);

    private static RuleLocalization PtPattern(string title, string description, string remediation) =>
        new(title, description, remediation, title, PatternMessagePt);

    private static readonly IReadOnlyDictionary<string, PatternRule> PatternRules = new Dictionary<string, PatternRule>
    {
        [SecurityPatternIds.SqlStringConcatenation] = new(Severity.High, ConfidenceLevel.Medium,
            "SQL built from dynamic strings",
            "A database command text is built by concatenation/interpolation — an injection candidate if any part is user-controlled.",
            "Use parameterized commands or an ORM; never concatenate input into SQL.",
            PtPattern("SQL montado a partir de strings dinâmicas",
                "O texto de um comando de banco é montado por concatenação/interpolação — candidato a injeção se alguma parte vier do usuário.",
                "Use comandos parametrizados ou um ORM; nunca concatene entrada em SQL.")),
        [SecurityPatternIds.WeakHash] = new(Severity.Medium, ConfidenceLevel.Medium,
            "Weak hash algorithm (MD5/SHA-1)",
            "MD5 and SHA-1 are broken for security purposes (collisions). Acceptable only for non-security checksums.",
            "Use SHA-256+ for integrity; PBKDF2/bcrypt/Argon2 for passwords.",
            PtPattern("Algoritmo de hash fraco (MD5/SHA-1)",
                "MD5 e SHA-1 estão quebrados para fins de segurança (colisões). Aceitáveis apenas para checksums sem valor de segurança.",
                "Use SHA-256 ou superior para integridade; PBKDF2/bcrypt/Argon2 para senhas.")),
        [SecurityPatternIds.WeakSymmetricCipher] = new(Severity.High, ConfidenceLevel.High,
            "Weak symmetric cipher (DES/3DES/RC2)",
            "Legacy ciphers with small block/key sizes are not considered secure.",
            "Use AES-GCM (or AES-CBC with HMAC) via System.Security.Cryptography.Aes.",
            PtPattern("Cifra simétrica fraca (DES/3DES/RC2)",
                "Cifras legadas com blocos/chaves pequenos não são consideradas seguras.",
                "Use AES-GCM (ou AES-CBC com HMAC) via System.Security.Cryptography.Aes.")),
        [SecurityPatternIds.BinaryFormatter] = new(Severity.High, ConfidenceLevel.High,
            "BinaryFormatter deserialization",
            "BinaryFormatter is insecure by design and removed in modern .NET; deserializing untrusted data is remote code execution.",
            "Replace with System.Text.Json, MessagePack or protobuf; never deserialize untrusted binary payloads.",
            PtPattern("Desserialização com BinaryFormatter",
                "BinaryFormatter é inseguro por design e foi removido do .NET moderno; desserializar dados não confiáveis é execução remota de código.",
                "Substitua por System.Text.Json, MessagePack ou protobuf; nunca desserialize payloads binários não confiáveis.")),
        [SecurityPatternIds.TypeNameHandling] = new(Severity.High, ConfidenceLevel.High,
            "Json.NET TypeNameHandling enabled",
            "TypeNameHandling other than None lets JSON payloads choose the .NET type to instantiate — a known RCE vector.",
            "Use TypeNameHandling.None or a strict SerializationBinder allowlist.",
            PtPattern("TypeNameHandling do Json.NET habilitado",
                "TypeNameHandling diferente de None permite que o JSON escolha o tipo .NET a instanciar — vetor conhecido de RCE.",
                "Use TypeNameHandling.None ou um SerializationBinder com allowlist estrita.")),
        [SecurityPatternIds.CertificateValidationDisabled] = new(Severity.High, ConfidenceLevel.Medium,
            "TLS certificate validation callback overridden",
            "Custom ServerCertificateValidationCallback often returns true unconditionally, disabling TLS trust.",
            "Remove the override or validate the certificate chain/pin explicitly.",
            PtPattern("Callback de validação de certificado TLS sobrescrito",
                "ServerCertificateValidationCallback customizado costuma retornar true incondicionalmente, desativando a confiança TLS.",
                "Remova a sobrescrita ou valide a cadeia de certificados/pin explicitamente.")),
        [SecurityPatternIds.LegacyTlsProtocol] = new(Severity.Medium, ConfidenceLevel.High,
            "Legacy TLS/SSL protocol pinned",
            "SSL 3.0, TLS 1.0 and TLS 1.1 are deprecated and disabled by modern platforms.",
            "Use SecurityProtocolType.SystemDefault (or Tls12/Tls13).",
            PtPattern("Protocolo TLS/SSL legado fixado",
                "SSL 3.0, TLS 1.0 e TLS 1.1 estão obsoletos e desativados nas plataformas modernas.",
                "Use SecurityProtocolType.SystemDefault (ou Tls12/Tls13).")),
        [SecurityPatternIds.XmlDtdProcessing] = new(Severity.Medium, ConfidenceLevel.High,
            "XML external entity processing enabled",
            "DtdProcessing.Parse or an XmlUrlResolver allows XXE attacks on untrusted XML.",
            "Set DtdProcessing.Prohibit and XmlResolver = null.",
            PtPattern("Processamento de entidades externas XML habilitado",
                "DtdProcessing.Parse ou um XmlUrlResolver permite ataques XXE em XML não confiável.",
                "Defina DtdProcessing.Prohibit e XmlResolver = null.")),
        [SecurityPatternIds.RequestValidationDisabled] = new(Severity.Medium, ConfidenceLevel.High,
            "ASP.NET request validation disabled",
            "[ValidateInput(false)] disables the framework's XSS guard for the action.",
            "Re-enable validation and encode output; use [AllowHtml] only on the specific property.",
            PtPattern("Validação de requisição do ASP.NET desativada",
                "[ValidateInput(false)] desativa a proteção contra XSS do framework para a action.",
                "Reative a validação e codifique a saída; use [AllowHtml] apenas na propriedade específica.")),
        [SecurityPatternIds.ProcessStartConcatenation] = new(Severity.Medium, ConfidenceLevel.Low,
            "Process started with dynamic command",
            "Process.Start with a concatenated command line is a command-injection candidate.",
            "Pass arguments via ArgumentList; never build shell commands from input.",
            PtPattern("Processo iniciado com comando dinâmico",
                "Process.Start com linha de comando concatenada é candidato a injeção de comando.",
                "Passe argumentos via ArgumentList; nunca monte comandos de shell a partir de entrada.")),
    };

    private sealed record ConfigRule(string Id, Severity Severity, string Title, string Description, string Remediation, string Message, RuleLocalization PtBr);

    private static readonly IReadOnlyList<ConfigRule> ConfigRules =
    [
        new(ConfigRuleIds.DebugEnabled, Severity.Medium,
            "Debug compilation enabled in web.config", "Debug builds leak detailed errors and disable optimizations.",
            "Set compilation debug=\"false\" in production.",
            "<compilation debug=\"true\"> ships debug builds and detailed errors to production.",
            new("Compilação em modo debug no web.config", "Builds de debug expõem erros detalhados e desativam otimizações.",
                "Defina compilation debug=\"false\" em produção.", "Compilação em modo debug no web.config",
                "<compilation debug=\"true\"> leva builds de debug e erros detalhados para produção.")),
        new(ConfigRuleIds.CustomErrorsOff, Severity.Medium,
            "Custom errors disabled in web.config", "Detailed exception pages are shown to remote clients.",
            "Use customErrors mode=\"RemoteOnly\" or \"On\".",
            "<customErrors mode=\"Off\"> exposes stack traces to remote clients.",
            new("Custom errors desativados no web.config", "Páginas de exceção detalhadas são exibidas a clientes remotos.",
                "Use customErrors mode=\"RemoteOnly\" ou \"On\".", "Custom errors desativados no web.config",
                "<customErrors mode=\"Off\"> expõe stack traces a clientes remotos.")),
        new(ConfigRuleIds.TraceEnabled, Severity.Low,
            "Tracing enabled in web.config", "trace.axd exposes request details.",
            "Disable tracing in production.",
            "<trace enabled=\"true\"> exposes request tracing (trace.axd).",
            new("Tracing habilitado no web.config", "trace.axd expõe detalhes das requisições.",
                "Desative o tracing em produção.", "Tracing habilitado no web.config",
                "<trace enabled=\"true\"> expõe o tracing de requisições (trace.axd).")),
        new(ConfigRuleIds.CookiesNotHttpOnly, Severity.Medium,
            "Cookies not HttpOnly in web.config", "Session cookies readable by script amplify XSS impact.",
            "Set httpOnlyCookies=\"true\".",
            "<httpCookies httpOnlyCookies=\"false\"> makes cookies readable by script.",
            new("Cookies sem HttpOnly no web.config", "Cookies de sessão legíveis por script amplificam o impacto de XSS.",
                "Defina httpOnlyCookies=\"true\".", "Cookies sem HttpOnly no web.config",
                "<httpCookies httpOnlyCookies=\"false\"> torna os cookies legíveis por script.")),
    ];

    public ScannerDescriptor Descriptor { get; } = new(
        Id: "security.patterns",
        Name: "Security Pattern Scanner",
        Version: "0.2.0",
        Category: FindingCategory.Security,
        Capabilities: ["code-patterns", "web-config"]);

    public IReadOnlyList<RuleSpec> Rules { get; } = BuildRules();

    public Task<ScanResult> ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        foreach (var fact in context.Languages.Values.SelectMany(l => l.Patterns))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!PatternRules.TryGetValue(fact.PatternId, out var rule))
            {
                continue; // a language adapter newer than this scanner; ignore rather than emit undeclared rules
            }

            context.Findings.Emit(new FindingCandidate(
                fact.PatternId,
                rule.Severity,
                rule.Confidence,
                Title: rule.Title,
                Message: $"{fact.Detail} in {fact.Symbol} (line {fact.Line}).",
                Evidence: new EvidenceCandidate(FilePath: fact.FilePath, LineStart: fact.Line, Symbol: fact.Symbol),
                Remediation: rule.Remediation,
                Data: new Dictionary<string, string> { ["detail"] = fact.Detail, ["pattern"] = fact.PatternId }));
        }

        return ScanConfigFilesAsync(context, cancellationToken);
    }

    private static async Task<ScanResult> ScanConfigFilesAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var configFiles = context.Workspace.EnumerateFiles("web.config")
            .Concat(context.Workspace.EnumerateFiles("Web.config"))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var path in configFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            XDocument xml;
            try
            {
                xml = XDocument.Parse(
                    await context.Workspace.ReadAllTextAsync(path, cancellationToken), LoadOptions.SetLineInfo);
            }
            catch (System.Xml.XmlException)
            {
                continue;
            }

            foreach (var element in xml.Descendants())
            {
                var name = element.Name.LocalName;
                var line = (element as System.Xml.IXmlLineInfo)?.LineNumber ?? 0;

                var ruleId = name switch
                {
                    "compilation" when IsTrue(element.Attribute("debug")) => ConfigRuleIds.DebugEnabled,
                    "customErrors" when string.Equals(element.Attribute("mode")?.Value, "Off", StringComparison.OrdinalIgnoreCase) => ConfigRuleIds.CustomErrorsOff,
                    "trace" when IsTrue(element.Attribute("enabled")) => ConfigRuleIds.TraceEnabled,
                    "httpCookies" when string.Equals(element.Attribute("httpOnlyCookies")?.Value, "false", StringComparison.OrdinalIgnoreCase) => ConfigRuleIds.CookiesNotHttpOnly,
                    _ => null,
                };

                if (ruleId is null)
                {
                    continue;
                }

                var rule = ConfigRules.Single(r => r.Id == ruleId);
                context.Findings.Emit(new FindingCandidate(
                    rule.Id, rule.Severity, ConfidenceLevel.High,
                    Title: rule.Title,
                    Message: rule.Message,
                    Evidence: new EvidenceCandidate(FilePath: path, LineStart: line == 0 ? null : line, Symbol: name),
                    Remediation: rule.Remediation));
            }
        }

        return ScanResult.Success();
    }

    private static bool IsTrue(XAttribute? attribute) =>
        string.Equals(attribute?.Value, "true", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<RuleSpec> BuildRules()
    {
        var rules = PatternRules
            .Select(kv => new RuleSpec(kv.Key, RulesVersion, FindingCategory.Security, kv.Value.Severity,
                kv.Value.Title, kv.Value.Description, kv.Value.Remediation,
                new Dictionary<string, RuleLocalization> { [Pt] = kv.Value.PtBr }))
            .ToList();

        rules.AddRange(ConfigRules.Select(r => new RuleSpec(r.Id, RulesVersion, FindingCategory.Security, r.Severity,
            r.Title, r.Description, r.Remediation,
            new Dictionary<string, RuleLocalization> { [Pt] = r.PtBr })));

        return rules;
    }
}
