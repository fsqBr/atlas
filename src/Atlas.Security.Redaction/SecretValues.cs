namespace Atlas.Security.Redaction;

/// <summary>
/// Decides whether a configuration value looks like a credential (so it must be fingerprinted, never
/// stored) and produces the masked preview that may accompany a fingerprint (at most 4 chars).
/// </summary>
public static class SecretValues
{
    public const int MaxPreviewChars = 4;

    /// <summary>"sk-a…" style preview: at most <see cref="MaxPreviewChars"/> leading characters, the rest elided.</summary>
    public static string Preview(string value, int maxChars = MaxPreviewChars)
    {
        ArgumentNullException.ThrowIfNull(value);
        var keep = Math.Clamp(maxChars, 0, MaxPreviewChars);
        var trimmed = value.Trim();
        return trimmed.Length <= keep ? new string('•', trimmed.Length) : trimmed[..keep] + "…";
    }

    /// <summary>
    /// Heuristic "this is a credential, not a name": a well-known token prefix, or a long opaque string
    /// with high character variety. Placeholders (${VAR}, $VAR, %VAR%, &lt;your-key&gt;) are never secrets.
    /// </summary>
    public static bool LooksLikeSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var v = value.Trim();
        if (v.StartsWith('$') || v.StartsWith('%') || v.StartsWith('<') || v.StartsWith("{{", StringComparison.Ordinal)
            || v.Contains("your-", StringComparison.OrdinalIgnoreCase) || v.Contains("changeme", StringComparison.OrdinalIgnoreCase)
            || v.Contains("xxxx", StringComparison.OrdinalIgnoreCase) || v.Contains("...", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var prefix in KnownPrefixes)
        {
            if (v.StartsWith(prefix, StringComparison.Ordinal) && v.Length >= prefix.Length + 8)
            {
                return true;
            }
        }

        if (v.Length < 20 || v.Contains(' '))
        {
            return false;
        }

        var classes = (v.Any(char.IsUpper) ? 1 : 0) + (v.Any(char.IsLower) ? 1 : 0) + (v.Any(char.IsDigit) ? 1 : 0)
            + (v.Any(ch => !char.IsLetterOrDigit(ch)) ? 1 : 0);
        return classes >= 3;
    }

    private static readonly string[] KnownPrefixes =
    [
        "sk-", "sk_live_", "sk_test_", "ghp_", "gho_", "ghu_", "github_pat_", "glpat-", "xoxb-", "xoxp-", "xapp-",
        "AIza", "hf_", "gsk_", "AKIA", "ASIA", "eyJ", "pcsk_", "pk-lf-", "sk-lf-", "r8_", "npm_", "dop_v1_", "atlas_pat_",
    ];
}
