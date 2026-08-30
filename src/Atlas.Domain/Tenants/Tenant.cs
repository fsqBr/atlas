namespace Atlas.Domain.Tenants;

/// <summary>
/// Isolation boundary for all Atlas data. Self-hosted deployments run
/// as a single tenant; the boundary exists from the first migration either way.
/// Not to be confused with a scanned Organization (a customer's GitHub/ADO org).
/// </summary>
public sealed class Tenant
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;

    /// <summary>Value of the identity token's tenant claim that maps to this tenant (e.g. Entra tid, Keycloak realm attribute).</summary>
    public string? ExternalKey { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private Tenant()
    {
    }

    public Tenant(Guid id, string name)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Tenant name must not be empty.", nameof(name));
        }

        Id = id;
        Name = name.Trim();
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public void Update(string name, string? externalKey)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Tenant name must not be empty.", nameof(name));
        }

        externalKey = string.IsNullOrWhiteSpace(externalKey) ? null : externalKey.Trim();
        if (externalKey is { Length: > 200 })
        {
            throw new ArgumentException("External key must be at most 200 characters.", nameof(externalKey));
        }

        Name = name.Trim();
        ExternalKey = externalKey;
    }
}
