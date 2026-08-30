using System.Globalization;
using Atlas.Domain.Findings;
using Atlas.Domain.Rules;
using Atlas.Language.Abstractions;
using Atlas.Scanner.Abstractions;

namespace Atlas.Scanner.Privacy;

/// <summary>
/// Sensitive data / PII inventory (.5) and data leakage candidates
/// (.6) from language pattern facts. Name-based: every finding is a
/// candidate with a confidence, never a claim that a value was exposed.
/// Field facts are aggregated per (type, category) — "Customer holds 6 contact
/// fields" — so an inventory of 100 columns is 10 findings, not 100. Leaks stay
/// one per sink. Categories follow LGPD's split between personal data (art. 5)
/// and sensitive personal data (art. 11). Rules are bilingual.
/// </summary>
public sealed class PrivacyScanner : IScanner
{
    public static class RuleIds
    {
        public const string Identifier = "privacy.pii.identifier";
        public const string Contact = "privacy.pii.contact";
        public const string Financial = "privacy.pii.financial";
        public const string Health = "privacy.pii.health";
        public const string Credential = "privacy.pii.credential";
        public const string Birth = "privacy.pii.birth";
        public const string LeakToLog = "privacy.leak.log";
        public const string LeakToException = "privacy.leak.exception";
    }

    private const string RulesVersion = "1.1.0";
    private const string Pt = "pt-BR";

    private sealed record Rule(string Id, Severity Severity, ConfidenceLevel Confidence, string Title, string Description, string Remediation, RuleLocalization PtBr);

    private const string FieldTitlePt = "{type}: {count} campo(s) — {categoryPt}";
    private const string FieldMessagePt = "{type} guarda {count} campo(s) de dados pessoais ({categoryPt}): {members} — {fileName}.";
    private const string LeakMessagePt = "{detail} em {symbol} (linha {line}).";

    private static RuleLocalization PtField(string title, string description, string remediation) => new(title, description, remediation, FieldTitlePt, FieldMessagePt);

    private static RuleLocalization PtLeak(string title, string description, string remediation) => new(title, description, remediation, title, LeakMessagePt);

    private static readonly IReadOnlyDictionary<string, string> CategoryPt = new Dictionary<string, string>
    {
        ["identifier"] = "identificadores", ["contact"] = "contato", ["financial"] = "financeiro", ["health"] = "saúde",
        ["credential"] = "credenciais", ["birth"] = "nascimento",
    };

    private static readonly IReadOnlyDictionary<string, Rule> FieldRules = new Dictionary<string, Rule>(StringComparer.Ordinal)
    {
        ["identifier"] = new(RuleIds.Identifier, Severity.Medium, ConfidenceLevel.Medium,
            "Personal identifiers stored (CPF, CNPJ, RG, passport…)",
            "Member names indicate government or tax identifiers — personal data under LGPD art. 5 / GDPR art. 4. They must be inventoried, minimized and protected at rest and in transit.",
            "Confirm the fields are necessary, encrypt or tokenize them at rest, restrict access and log access; document them in the data inventory (RoPA).",
            PtField("Identificadores pessoais armazenados (CPF, CNPJ, RG, passaporte…)",
                "Nomes de membros indicam identificadores governamentais ou fiscais — dado pessoal (LGPD art. 5). Devem ser inventariados, minimizados e protegidos em repouso e em trânsito.",
                "Confirme a necessidade dos campos, cifre ou tokenize em repouso, restrinja e registre acessos; documente no inventário de dados (ROPA).")),
        ["contact"] = new(RuleIds.Contact, Severity.Low, ConfidenceLevel.Medium,
            "Contact data stored (e-mail, phone, address)",
            "Contact details are personal data; they need a lawful basis, retention rules and protection against leakage.",
            "Document purpose and retention; avoid copying contact data into logs, exports and analytics events.",
            PtField("Dados de contato armazenados (e-mail, telefone, endereço)",
                "Dados de contato são dados pessoais; exigem base legal, regra de retenção e proteção contra vazamento.",
                "Documente finalidade e retenção; evite copiar dados de contato para logs, exportações e eventos de analytics.")),
        ["financial"] = new(RuleIds.Financial, Severity.High, ConfidenceLevel.Medium,
            "Financial / payment data stored (card, CVV, bank account, income)",
            "Card and bank data fall under PCI DSS and LGPD; CVV must never be stored; income is sensitive commercial data.",
            "Tokenize card data through the payment provider, never persist CVV, encrypt account numbers and restrict access by role.",
            PtField("Dados financeiros / de pagamento armazenados (cartão, CVV, conta, renda)",
                "Dados de cartão e conta estão sob PCI DSS e LGPD; CVV nunca deve ser armazenado; renda é dado sensível comercialmente.",
                "Tokenize o cartão pelo provedor de pagamento, nunca persista CVV, cifre números de conta e restrinja acesso por perfil.")),
        ["health"] = new(RuleIds.Health, Severity.High, ConfidenceLevel.Medium,
            "Health data stored (sensitive personal data)",
            "Health information is sensitive personal data (LGPD art. 11 / GDPR art. 9): explicit consent or a specific legal basis, strict access control and encryption are required.",
            "Confirm the legal basis, encrypt at rest, segregate access, and keep an access log; minimize what is collected.",
            PtField("Dados de saúde armazenados (dado pessoal sensível)",
                "Informação de saúde é dado pessoal sensível (LGPD art. 11): exige consentimento específico ou base legal própria, controle de acesso estrito e criptografia.",
                "Confirme a base legal, cifre em repouso, segregue o acesso e mantenha trilha de acesso; minimize a coleta.")),
        ["credential"] = new(RuleIds.Credential, Severity.Medium, ConfidenceLevel.Medium,
            "Credential fields on a data model (password / secret answer)",
            "Password-like members on a model suggest credentials travel or persist as data; if stored in clear or reversibly, it is a breach waiting to happen.",
            "Store only salted hashes (PBKDF2/bcrypt/Argon2), keep credentials out of DTOs returned by APIs and out of logs.",
            PtField("Campos de credencial em modelo de dados (senha / resposta secreta)",
                "Membros tipo senha em um modelo sugerem que credenciais trafegam ou persistem como dado; se em claro ou reversível, é um incidente esperando acontecer.",
                "Armazene apenas hashes com salt (PBKDF2/bcrypt/Argon2), mantenha credenciais fora de DTOs de API e de logs.")),
        ["birth"] = new(RuleIds.Birth, Severity.Low, ConfidenceLevel.Medium,
            "Date of birth stored",
            "Birth dates are personal data and a common identity-verification factor; combined with name and address they enable identity fraud.",
            "Store only if needed (age checks can keep just the year or a boolean); protect like other identifiers.",
            PtField("Data de nascimento armazenada",
                "Datas de nascimento são dados pessoais e fator comum de verificação de identidade; combinadas com nome e endereço viabilizam fraude.",
                "Armazene só se necessário (checagens de idade podem guardar apenas o ano ou um booleano); proteja como os demais identificadores.")),
    };

    private static readonly IReadOnlyDictionary<string, Rule> LeakRules = new Dictionary<string, Rule>(StringComparer.Ordinal)
    {
        [PrivacyPatternIds.LeakToLog] = new(RuleIds.LeakToLog, Severity.High, ConfidenceLevel.Medium,
            "Personal data written to a log",
            "A value whose name indicates personal data is passed to a logging call. Logs are copied, retained and read widely — this is the most common real-world leakage path.",
            "Log identifiers (ids, correlation ids) instead of personal values; mask or hash when the value is needed for support.",
            PtLeak("Dado pessoal escrito em log",
                "Um valor cujo nome indica dado pessoal é passado a uma chamada de log. Logs são copiados, retidos e lidos amplamente — é o caminho de vazamento mais comum na prática.",
                "Registre identificadores (ids, correlation ids) em vez de valores pessoais; mascare ou faça hash quando o valor for necessário para suporte.")),
        [PrivacyPatternIds.LeakToException] = new(RuleIds.LeakToException, Severity.Medium, ConfidenceLevel.Medium,
            "Personal data in an exception message",
            "Exception messages end up in logs, error pages and monitoring tools; personal values inside them leak through every one of those channels.",
            "Keep personal values out of exception messages; attach an opaque reference instead.",
            PtLeak("Dado pessoal em mensagem de exceção",
                "Mensagens de exceção vão para logs, páginas de erro e ferramentas de monitoramento; valores pessoais dentro delas vazam por todos esses canais.",
                "Mantenha valores pessoais fora das mensagens de exceção; anexe uma referência opaca.")),
    };

    public ScannerDescriptor Descriptor { get; } = new(
        Id: "privacy.data",
        Name: "Sensitive Data & Leakage Scanner",
        Version: "0.2.0",
        Category: FindingCategory.Data,
        Capabilities: ["pii-inventory", "leakage-candidates"]);

    public IReadOnlyList<RuleSpec> Rules { get; } = FieldRules.Values.Concat(LeakRules.Values)
        .Select(r => new RuleSpec(r.Id, RulesVersion, FindingCategory.Data, r.Severity, r.Title, r.Description, r.Remediation,
            new Dictionary<string, RuleLocalization> { [Pt] = r.PtBr }))
        .ToList();

    public Task<ScanResult> ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var groups = new Dictionary<(string Type, string Category), List<PatternFact>>();

        foreach (var fact in context.Languages.Values.SelectMany(l => l.Patterns))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (fact.PatternId.StartsWith(PrivacyPatternIds.FieldPrefix, StringComparison.Ordinal))
            {
                var category = fact.PatternId[PrivacyPatternIds.FieldPrefix.Length..];
                if (!FieldRules.ContainsKey(category))
                {
                    continue;
                }

                var dot = fact.Symbol.LastIndexOf('.');
                var type = dot > 0 ? fact.Symbol[..dot] : fact.Symbol;
                (groups.TryGetValue((type, category), out var list) ? list : groups[(type, category)] = []).Add(fact);
            }
            else if (LeakRules.TryGetValue(fact.PatternId, out var leakRule))
            {
                // Leaking financial, health or credential values is worse than leaking a phone number.
                var escalate = fact.Detail.Contains("[financial]", StringComparison.Ordinal)
                    || fact.Detail.Contains("[health]", StringComparison.Ordinal)
                    || fact.Detail.Contains("[credential]", StringComparison.Ordinal);
                var severity = escalate && leakRule.Severity < Severity.Critical ? leakRule.Severity + 1 : leakRule.Severity;
                var category = fact.Detail.Contains('[') ? fact.Detail[(fact.Detail.LastIndexOf('[') + 1)..].TrimEnd(']') : "unknown";

                context.Findings.Emit(new FindingCandidate(
                    leakRule.Id, severity, leakRule.Confidence,
                    Title: leakRule.Title,
                    Message: $"{fact.Detail} in {fact.Symbol} (line {fact.Line}).",
                    Evidence: new EvidenceCandidate(FilePath: fact.FilePath, LineStart: fact.Line, Symbol: fact.Symbol),
                    Remediation: leakRule.Remediation,
                    Data: new Dictionary<string, string> { ["detail"] = fact.Detail, ["pattern"] = fact.PatternId, ["dataCategory"] = category }));
            }
        }

        foreach (var ((type, category), facts) in groups.OrderBy(g => g.Key.Type, StringComparer.Ordinal).ThenBy(g => g.Key.Category, StringComparer.Ordinal))
        {
            var rule = FieldRules[category];
            var first = facts.OrderBy(f => f.Line).First();
            var members = facts.Select(f => f.Symbol[(f.Symbol.LastIndexOf('.') + 1)..]).Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal).ToList();
            var fileName = Path.GetFileName(first.FilePath);

            context.Findings.Emit(new FindingCandidate(
                rule.Id, rule.Severity, rule.Confidence,
                Title: $"{type}: {members.Count} {category} field(s)",
                Message: $"{type} holds {members.Count} personal-data field(s) ({category}): {string.Join(", ", members)} — {fileName}.",
                Evidence: new EvidenceCandidate(FilePath: first.FilePath, LineStart: first.Line, Symbol: $"{type}#{category}"),
                Remediation: rule.Remediation,
                Data: new Dictionary<string, string>
                {
                    ["type"] = type,
                    ["count"] = members.Count.ToString(CultureInfo.InvariantCulture),
                    ["members"] = string.Join(", ", members),
                    ["dataCategory"] = category,
                    ["categoryPt"] = CategoryPt.GetValueOrDefault(category, category),
                    ["fileName"] = fileName,
                }));
        }

        return Task.FromResult(ScanResult.Success());
    }
}
