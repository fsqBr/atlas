using System.Text.RegularExpressions;
using Atlas.Domain.Findings;

namespace Atlas.Scanner.Secrets;

public sealed record SecretDetector(
    string Id,
    string Name,
    string NamePtBr,
    Severity Severity,
    ConfidenceLevel Confidence,
    Regex Pattern,
    bool RequiresEntropy,
    string Remediation,
    string RemediationPtBr);

/// <summary>
/// Curated, versioned secret detectors (.4). Well-known token
/// formats get high confidence; the generic assignment detector is a heuristic
/// gated by entropy and a placeholder allowlist to keep false positives down.
/// </summary>
public static class SecretDetectors
{
    public const string Version = "2026.08";

    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    /// <summary>Values that are obviously templates, never real secrets.</summary>
    public static readonly Regex Placeholder = new(
        @"(?i)(changeme|change-me|example|your[_-]?|placeholder|dummy|xxxx|\*\*\*|<[^>]+>|\$\{|%\(|\{\{|todo|redacted|sample|passw0rd|^password$|^secret$)",
        Opts);

    public static readonly IReadOnlyList<SecretDetector> All =
    [
        new("secrets.private-key", "Private key material", "Material de chave privada", Severity.Critical, ConfidenceLevel.High,
            new Regex(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY-----", Opts), false,
            "Remove the key from the repository, rotate it, and load keys from a secret store at runtime.",
            "Remova a chave do repositório, rotacione-a e carregue chaves de um cofre de segredos em tempo de execução."),

        new("secrets.aws-access-key", "AWS access key id", "Chave de acesso AWS", Severity.High, ConfidenceLevel.High,
            new Regex(@"\b((?:AKIA|ASIA)[0-9A-Z]{16})\b", Opts), false,
            "Rotate the key in IAM and move credentials to environment/secret manager.",
            "Rotacione a chave no IAM e mova as credenciais para ambiente/gerenciador de segredos."),

        new("secrets.github-token", "GitHub token", "Token do GitHub", Severity.High, ConfidenceLevel.High,
            new Regex(@"\b((?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36}|github_pat_[A-Za-z0-9_]{22,})\b", Opts), false,
            "Revoke the token in GitHub settings and use a secret store or GitHub App installation.",
            "Revogue o token nas configurações do GitHub e use um cofre de segredos ou instalação de GitHub App."),

        new("secrets.azure-storage-key", "Azure Storage account key", "Chave de Storage Account do Azure", Severity.High, ConfidenceLevel.High,
            new Regex(@"AccountKey=([A-Za-z0-9+/=]{86,90})", Opts), false,
            "Regenerate the storage key; prefer managed identity or Key Vault references.",
            "Regenere a chave de storage; prefira managed identity ou referências ao Key Vault."),

        new("secrets.stripe-live-key", "Stripe live secret key", "Chave secreta live do Stripe", Severity.Critical, ConfidenceLevel.High,
            new Regex(@"\b((?:sk|rk)_live_[0-9a-zA-Z]{20,})\b", Opts), false,
            "Roll the key in the Stripe dashboard and load it from configuration secrets.",
            "Troque a chave no dashboard do Stripe e carregue-a de segredos de configuração."),

        new("secrets.slack-token", "Slack token", "Token do Slack", Severity.High, ConfidenceLevel.High,
            new Regex(@"\b(xox[baprs]-[A-Za-z0-9-]{10,})\b", Opts), false,
            "Revoke the token in Slack and store replacements outside source control.",
            "Revogue o token no Slack e guarde o substituto fora do controle de versão."),

        new("secrets.google-api-key", "Google API key", "Chave de API do Google", Severity.High, ConfidenceLevel.High,
            new Regex(@"\b(AIza[0-9A-Za-z_-]{35})\b", Opts), false,
            "Restrict and rotate the key in Google Cloud Console.",
            "Restrinja e rotacione a chave no Google Cloud Console."),

        new("secrets.jwt", "JSON Web Token", "JSON Web Token", Severity.Medium, ConfidenceLevel.Medium,
            new Regex(@"\b(eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,})\b", Opts), false,
            "Tokens in source are usually test fixtures; confirm they are expired and carry no real claims.",
            "Tokens no código costumam ser fixtures de teste; confirme que estão expirados e não carregam claims reais."),

        new("secrets.connection-string-password", "Connection string with password", "Connection string com senha", Severity.High, ConfidenceLevel.Medium,
            new Regex(@"(?i)(?:server|data source|host|initial catalog|database)\s*=[^;]*;[^\n]*?(?:password|pwd)\s*=\s*([^;""'\s]{4,})", Opts), false,
            "Move connection strings to environment variables or a secret store; use integrated auth where possible.",
            "Mova connection strings para variáveis de ambiente ou um cofre de segredos; use autenticação integrada quando possível."),

        new("secrets.generic-assignment", "Hard-coded credential assignment", "Credencial fixa em atribuição", Severity.Medium, ConfidenceLevel.Low,
            new Regex(@"(?i)\b(?:api[_-]?key|apikey|client[_-]?secret|access[_-]?token|auth[_-]?token|secret[_-]?key|password|passwd|pwd)\b\s*[:=]\s*[""']([^""']{8,})[""']", Opts), true,
            "Load credentials from configuration/secret stores; never commit literal values.",
            "Carregue credenciais de configuração/cofres de segredos; nunca faça commit de valores literais."),
    ];

    /// <summary>File names that are secrets by their nature (.4 "private keys").</summary>
    public static readonly Regex KeyFileName = new(
        @"(?i)(\.pfx|\.p12|\.pem|\.key|\.jks|\.keystore|\.ppk|(^|[\\/])id_(rsa|dsa|ecdsa|ed25519))$", Opts);

    public const string KeyFileRuleId = "secrets.key-file";

    public static double ShannonEntropy(string value)
    {
        if (value.Length == 0)
        {
            return 0;
        }

        var counts = new Dictionary<char, int>();
        foreach (var c in value)
        {
            counts[c] = counts.GetValueOrDefault(c) + 1;
        }

        double entropy = 0;
        foreach (var count in counts.Values)
        {
            var p = (double)count / value.Length;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }
}
