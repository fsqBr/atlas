using Atlas.Domain.Security;

namespace Atlas.Domain.Tests;

public class ApiTokenTests
{
    [Fact]
    public void Creates_a_prefixed_secret_stored_only_as_a_hash()
    {
        var (token, secret) = ApiToken.Create(Guid.NewGuid(), " ci-gate ", "Admin", "felipe", DateTimeOffset.UtcNow.AddDays(30));

        Assert.StartsWith(ApiToken.Prefix, secret);
        Assert.True(ApiToken.LooksLikeToken(secret));
        Assert.Equal("ci-gate", token.Name);
        Assert.Equal(ApiToken.Roles.Admin, token.Role);
        Assert.DoesNotContain(secret, token.Hash);
        Assert.Equal(64, token.Hash.Length);
        Assert.StartsWith(ApiToken.Prefix, token.Hint);
        Assert.EndsWith("…", token.Hint);
        Assert.True(token.Matches(secret));
        Assert.False(token.Matches(secret + "x"));
        Assert.True(token.IsActive(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Expiry_and_revocation_deactivate()
    {
        var (token, _) = ApiToken.Create(Guid.NewGuid(), "t", "analyst", "x", DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.True(token.IsActive(DateTimeOffset.UtcNow));
        Assert.False(token.IsActive(DateTimeOffset.UtcNow.AddMinutes(6)));

        token.Revoke();
        Assert.NotNull(token.RevokedAtUtc);
        Assert.False(token.IsActive(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Validates_inputs()
    {
        Assert.Throws<ArgumentException>(() => ApiToken.Create(Guid.NewGuid(), "", "analyst", "x", null));
        Assert.Throws<ArgumentException>(() => ApiToken.Create(Guid.NewGuid(), "t", "root", "x", null));
        Assert.Throws<ArgumentException>(() => ApiToken.Create(Guid.NewGuid(), "t", "analyst", "x", DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.False(ApiToken.LooksLikeToken("Bearer abc"));
        Assert.False(ApiToken.LooksLikeToken(null));
    }
}
