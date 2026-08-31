using Atlas.Domain.Tenants;

namespace Atlas.Application.Tenants;

/// <summary>
/// Who the current unit of work acts for. The API resolves it per
/// request from the identity token (or the X-Atlas-Tenant header when auth is
/// off); the worker and background services run in system scope and see every
/// tenant. Repositories never take a tenant id: the DbContext applies it as a
/// global query filter, so a request cannot read another tenant's rows by
/// accident.
/// </summary>
public interface ITenantContext
{
    /// <summary>Null = system scope (no filter). Throws when a request has not been resolved yet — fail closed.</summary>
    Guid? TenantId { get; }

    string? TenantName { get; }

    /// <summary>Who is acting: identity-token subject, e-mail, or "token:{id}" for service tokens. Null in system scope.</summary>
    string? Subject { get; }

    string? SubjectName { get; }

    /// <summary>Tenant administrators (and system scope, and installations without authentication) see and may change every assessment.</summary>
    bool IsAdmin { get; }
}

public static class TenantContextExtensions
{
    /// <summary>Tenant to stamp on new rows: the resolved one, or the default tenant in system scope.</summary>
    public static Guid Require(this ITenantContext context) => context.TenantId ?? WellKnownTenants.DefaultId;
}

/// <summary>System scope: worker, migrations, background jobs.</summary>
public sealed class SystemTenantContext : ITenantContext
{
    public static readonly SystemTenantContext Instance = new();

    public Guid? TenantId => null;

    public string? TenantName => null;

    public string? Subject => null;

    public string? SubjectName => null;

    public bool IsAdmin => true;
}

public interface ITenantRepository
{
    Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken cancellationToken);

    Task<Tenant?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<Tenant?> GetByExternalKeyAsync(string externalKey, CancellationToken cancellationToken);

    void Add(Tenant tenant);
}
