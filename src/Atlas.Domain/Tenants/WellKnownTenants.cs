namespace Atlas.Domain.Tenants;

public static class WellKnownTenants
{
    /// <summary>
    /// The single tenant of a self-hosted deployment. Cloud deployments
    /// create real tenants; the schema is identical either way.
    /// </summary>
    public static readonly Guid DefaultId = Guid.Parse("00000000-0000-0000-0000-000000000001");
}
