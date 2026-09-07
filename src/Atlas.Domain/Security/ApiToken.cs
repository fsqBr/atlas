using System.Security.Cryptography;
using System.Text;

namespace Atlas.Domain.Security;

/// <summary>
/// A long-lived credential for machines (CI pipelines, scripts): shown once at
/// creation, stored only as a SHA-256 hash, bound to one tenant and one role,
/// optionally expiring, revocable. Never a substitute for user sign-in.
/// </summary>
public sealed class ApiToken
{
    public const string Prefix = "atlas_pat_";
    public const int MaxNameLength = 100;

    public static class Roles
    {
        public const string Analyst = "analyst";
        public const string Admin = "admin";
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = null!;

    /// <summary>First characters of the secret, for recognition in lists ("atlas_pat_3f9a…").</summary>
    public string Hint { get; private set; } = null!;
    public string Hash { get; private set; } = null!;
    public string Role { get; private set; } = null!;
    public string CreatedBy { get; private set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? ExpiresAtUtc { get; private set; }
    public DateTimeOffset? LastUsedAtUtc { get; private set; }
    public DateTimeOffset? RevokedAtUtc { get; private set; }

    private ApiToken()
    {
    }

    private ApiToken(Guid id, Guid tenantId, string name, string hint, string hash, string role, string createdBy, DateTimeOffset? expiresAtUtc)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        Hint = hint;
        Hash = hash;
        Role = role;
        CreatedBy = createdBy;
        CreatedAtUtc = DateTimeOffset.UtcNow;
        ExpiresAtUtc = expiresAtUtc;
    }

    /// <summary>Creates the token and returns the one-time secret alongside it.</summary>
    public static (ApiToken Token, string Secret) Create(Guid tenantId, string name, string role, string createdBy, DateTimeOffset? expiresAtUtc)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength)
        {
            throw new ArgumentException($"Token name is required (max {MaxNameLength} characters).", nameof(name));
        }

        role = role.Trim().ToLowerInvariant();
        if (role is not (Roles.Analyst or Roles.Admin))
        {
            throw new ArgumentException("Role must be 'analyst' or 'admin'.", nameof(role));
        }

        if (expiresAtUtc is { } exp && exp <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentException("Expiry must be in the future.", nameof(expiresAtUtc));
        }

        var secret = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var token = new ApiToken(Guid.NewGuid(), tenantId, name.Trim(), secret[..(Prefix.Length + 6)] + "…", ComputeHash(secret), role, string.IsNullOrWhiteSpace(createdBy) ? "anonymous" : createdBy.Trim(), expiresAtUtc);
        return (token, secret);
    }

    public static bool LooksLikeToken(string? value) => value is not null && value.StartsWith(Prefix, StringComparison.Ordinal) && value.Length > Prefix.Length + 20;

    public static string ComputeHash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public bool IsActive(DateTimeOffset now) => RevokedAtUtc is null && (ExpiresAtUtc is null || ExpiresAtUtc > now);

    public bool Matches(string secret) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash), Encoding.ASCII.GetBytes(ComputeHash(secret)));

    public void Touch(DateTimeOffset now) => LastUsedAtUtc = now;

    public void Revoke() => RevokedAtUtc ??= DateTimeOffset.UtcNow;

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
