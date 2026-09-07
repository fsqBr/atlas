using Atlas.Domain.Tenants;

namespace Atlas.Domain.Tests.Tenants;

public class TenantTests
{
    [Fact]
    public void Creates_tenant_with_trimmed_name()
    {
        var tenant = new Tenant(Guid.NewGuid(), "  Acme  ");

        Assert.Equal("Acme", tenant.Name);
        Assert.NotEqual(default, tenant.CreatedAtUtc);
    }

    [Fact]
    public void Rejects_empty_id()
    {
        Assert.Throws<ArgumentException>(() => new Tenant(Guid.Empty, "Acme"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_blank_name(string name)
    {
        Assert.Throws<ArgumentException>(() => new Tenant(Guid.NewGuid(), name));
    }
}
