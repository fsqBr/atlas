using Atlas.Application.Assessments;
using Atlas.Application.Tenants;
using Atlas.Domain.Security;
using Microsoft.Extensions.Logging;

namespace Atlas.Application.Security;

public interface IApiTokenRepository
{
    Task<IReadOnlyList<ApiToken>> ListAsync(CancellationToken cancellationToken);

    Task<ApiToken?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Lookup by hash across tenants — authentication happens before the tenant is known.</summary>
    Task<ApiToken?> FindByHashAsync(string hash, CancellationToken cancellationToken);

    void Add(ApiToken token);
}

public sealed record ApiTokenSummary(Guid Id, string Name, string Hint, string Role, string CreatedBy, DateTimeOffset CreatedAtUtc, DateTimeOffset? ExpiresAtUtc, DateTimeOffset? LastUsedAtUtc, DateTimeOffset? RevokedAtUtc, bool Active);

public sealed record ApiTokenCreated(ApiTokenSummary Token, string Secret);

/// <summary>Who a valid token authenticates as: its tenant and role.</summary>
public sealed record ApiTokenIdentity(Guid TokenId, Guid TenantId, string Name, string Role);

/// <summary>
/// Machine credentials for CI and scripts. Admin-only management; the secret is
/// returned exactly once. Validation is a hash lookup plus expiry/revocation
/// check; last use is recorded at most once a minute to keep it cheap.
/// </summary>
public sealed class ApiTokenService(IApiTokenRepository repository, IUnitOfWork unitOfWork, ITenantContext tenant, ILogger<ApiTokenService> logger)
{
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    public async Task<IReadOnlyList<ApiTokenSummary>> ListAsync(CancellationToken cancellationToken) =>
        (await repository.ListAsync(cancellationToken)).OrderByDescending(t => t.CreatedAtUtc).Select(ToSummary).ToList();

    public async Task<ApiTokenCreated> CreateAsync(string name, string role, string createdBy, DateTimeOffset? expiresAtUtc, CancellationToken cancellationToken)
    {
        var (token, secret) = ApiToken.Create(tenant.Require(), name, role, createdBy, expiresAtUtc);
        repository.Add(token);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        logger.LogInformation("API token {Name} ({Role}) created by {Actor}.", token.Name, token.Role, token.CreatedBy);
        return new ApiTokenCreated(ToSummary(token), secret);
    }

    public async Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        var token = await repository.GetAsync(id, cancellationToken);
        if (token is null)
        {
            return false;
        }

        token.Revoke();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        logger.LogInformation("API token {Name} revoked.", token.Name);
        return true;
    }

    /// <summary>Null when the secret is unknown, revoked or expired.</summary>
    public async Task<ApiTokenIdentity?> AuthenticateAsync(string secret, CancellationToken cancellationToken)
    {
        if (!ApiToken.LooksLikeToken(secret))
        {
            return null;
        }

        var token = await repository.FindByHashAsync(ApiToken.ComputeHash(secret), cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (token is null || !token.Matches(secret) || !token.IsActive(now))
        {
            return null;
        }

        if (token.LastUsedAtUtc is null || now - token.LastUsedAtUtc > TouchInterval)
        {
            token.Touch(now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return new ApiTokenIdentity(token.Id, token.TenantId, token.Name, token.Role);
    }

    private static ApiTokenSummary ToSummary(ApiToken t) =>
        new(t.Id, t.Name, t.Hint, t.Role, t.CreatedBy, t.CreatedAtUtc, t.ExpiresAtUtc, t.LastUsedAtUtc, t.RevokedAtUtc, t.IsActive(DateTimeOffset.UtcNow));
}
