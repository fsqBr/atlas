using Atlas.Domain.AiEstate;

namespace Atlas.Application.AiEstate;

public interface ITenantAiEstateSettingsRepository
{
    /// <summary>Always by explicit tenant id: the worker runs in system scope and must not rely on the ambient filter.</summary>
    Task<TenantAiEstateSettings?> GetForTenantAsync(Guid tenantId, CancellationToken cancellationToken);

    void Add(TenantAiEstateSettings settings);

    void Remove(TenantAiEstateSettings settings);
}
