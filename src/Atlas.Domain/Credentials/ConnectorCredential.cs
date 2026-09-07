using System.Text.RegularExpressions;

namespace Atlas.Domain.Credentials;

/// <summary>
/// A named secret used by connectors to reach private sources (git token, PAT).
/// The secret is stored only as an envelope produced by the platform's secret
/// cipher (AES-GCM under the master key) and is never exposed through read
/// APIs — callers can rotate or delete it, not read it (SECURITY.md).
/// </summary>
public sealed partial class ConnectorCredential
{
    public const int MaxNameLength = 100;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = null!;
    public string? Username { get; private set; }
    public string? Description { get; private set; }
    public byte[] Envelope { get; private set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public DateTimeOffset? LastUsedAtUtc { get; private set; }

    private ConnectorCredential()
    {
    }

    public ConnectorCredential(Guid id, Guid tenantId, string name, string? username, string? description, byte[] envelope)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Credential id must not be empty.", nameof(id));
        }

        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        Id = id;
        TenantId = tenantId;
        Name = ValidateName(name);
        CreatedAtUtc = DateTimeOffset.UtcNow;
        Rotate(username, description, envelope);
    }

    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= MaxNameLength && NamePattern().IsMatch(name);

    public static string ValidateName(string? name) =>
        IsValidName(name)
            ? name!
            : throw new ArgumentException(
                $"Credential name must be 1-{MaxNameLength} characters of letters, digits, '.', '_' or '-'.", nameof(name));

    public void Rotate(string? username, string? description, byte[] envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Length == 0)
        {
            throw new ArgumentException("Envelope must not be empty.", nameof(envelope));
        }

        Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Envelope = envelope;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public void MarkUsed() => LastUsedAtUtc = DateTimeOffset.UtcNow;

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex NamePattern();
}
