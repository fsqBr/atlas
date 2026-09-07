using System.Security.Cryptography;
using System.Text;

namespace Atlas.Domain.Findings;

/// <summary>
/// Stable identity of a finding across scans:
/// hash(rule id + rule major version + repository + normalized path + symbol-or-snippet-hash).
/// Line numbers are deliberately excluded — an added import must not create a "new" finding.
/// </summary>
public static class FindingFingerprint
{
    public static string Compute(
        string ruleId,
        int ruleMajorVersion,
        string repositoryKey,
        string? filePath,
        string? symbolOrSnippetHash)
    {
        if (string.IsNullOrWhiteSpace(ruleId))
        {
            throw new ArgumentException("Rule id must not be empty.", nameof(ruleId));
        }

        var material = string.Join('\n',
            ruleId.Trim().ToLowerInvariant(),
            ruleMajorVersion.ToString(),
            repositoryKey.Trim().ToLowerInvariant(),
            NormalizePath(filePath),
            (symbolOrSnippetHash ?? string.Empty).Trim());

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>Separators unified, leading "./" removed, case-insensitive — the same file on Windows and Linux hashes alike.</summary>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.TrimStart('/').ToLowerInvariant();
    }

    public static int MajorVersionOf(string version)
    {
        var head = version.Split('.', 2)[0].TrimStart('v', 'V');
        return int.TryParse(head, out var major) ? major : 0;
    }
}
