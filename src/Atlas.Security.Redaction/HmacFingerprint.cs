using System.Security.Cryptography;
using System.Text;

namespace Atlas.Security.Redaction;

/// <summary>
/// The only thing that may be persisted about a secret value: HMAC-SHA256 under the
/// installation key, truncated to 32 hex chars — the same shape the secrets scanner has used since
/// v0.1. Never a plain hash (rainbow-table friendly) and never the value.
/// </summary>
public static class HmacFingerprint
{
    public const int Length = 32;

    /// <summary>Keyed fingerprint; with a null key the fingerprint is process-ephemeral (callers must warn, never silently persist).</summary>
    public static string Compute(byte[]? key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var effectiveKey = key ?? EphemeralKey.Value;
        return Convert.ToHexStringLower(HMACSHA256.HashData(effectiveKey, Encoding.UTF8.GetBytes(value)))[..Length];
    }

    /// <summary>Decodes Atlas:Secrets:HmacKeyBase64; null/blank/invalid → null (ephemeral).</summary>
    public static byte[]? KeyFromBase64(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        try
        {
            var key = Convert.FromBase64String(base64);
            return key.Length >= 16 ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static readonly Lazy<byte[]> EphemeralKey = new(() => RandomNumberGenerator.GetBytes(32));
}
