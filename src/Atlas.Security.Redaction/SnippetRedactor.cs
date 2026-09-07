using System.Text.RegularExpressions;

namespace Atlas.Security.Redaction;

/// <summary>
/// Belt and braces before a snippet leaves the environment or is persisted: the values of
/// obvious credentials, private key blocks, bearer tokens and long opaque tokens become "***".
/// The secrets scanner already keeps secrets findings out; this covers a password that happens
/// to sit next to the code being fixed, or a token inside a configuration file being inventoried.
/// Moved here from Atlas.Application so scanners can share it without depending on the core.
/// </summary>
public static partial class SnippetRedactor
{
    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|secret|token|api[_-]?key|apikey|client[_-]?secret|access[_-]?key|accountkey|sharedaccesskey|private[_-]?key)\b(\s*[=:]\s*)([""']?)([^""';,\s)]+)")]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9._\-]{16,}")]
    private static partial Regex Bearer();

    [GeneratedRegex(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b")]
    private static partial Regex AwsKey();

    [GeneratedRegex(@"\b(sk|ghp|gho|xox[abp]|atlas_pat)[_-][A-Za-z0-9_\-]{12,}\b")]
    private static partial Regex KnownPrefixes();

    public static string Redact(string text, out int replacements)
    {
        var count = 0;
        string Count(string replaced)
        {
            count++;
            return replaced;
        }

        var result = PrivateKey().Replace(text, _ => Count("-----BEGIN PRIVATE KEY-----***-----END PRIVATE KEY-----"));
        result = KeyValue().Replace(result, m => Count($"{m.Groups[1].Value}{m.Groups[2].Value}{m.Groups[3].Value}***"));
        result = Bearer().Replace(result, _ => Count("Bearer ***"));
        result = AwsKey().Replace(result, m => Count($"{m.Groups[1].Value}***"));
        result = KnownPrefixes().Replace(result, m => Count($"{m.Groups[1].Value}_***"));
        replacements = count;
        return result;
    }

    public static string Redact(string text) => Redact(text, out _);
}
