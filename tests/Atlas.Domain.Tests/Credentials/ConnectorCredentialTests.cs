using Atlas.Domain.Credentials;
using Atlas.Domain.Tenants;

namespace Atlas.Domain.Tests.Credentials;

public class ConnectorCredentialTests
{
    [Theory]
    [InlineData("github-org")]
    [InlineData("ado.pat_2026")]
    [InlineData("A")]
    public void Accepts_safe_names(string name) => Assert.True(ConnectorCredential.IsValidName(name));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("has space")]
    [InlineData("slash/name")]
    [InlineData("ção")]
    public void Rejects_unsafe_names(string name) => Assert.False(ConnectorCredential.IsValidName(name));

    [Fact]
    public void Rotate_replaces_envelope_and_metadata_but_keeps_identity()
    {
        var credential = new ConnectorCredential(Guid.NewGuid(), WellKnownTenants.DefaultId, "gh", " user ", null, [1, 2, 3]);
        var created = credential.CreatedAtUtc;

        credential.Rotate(null, "rotated", [9]);

        Assert.Equal("gh", credential.Name);
        Assert.Null(credential.Username);
        Assert.Equal("rotated", credential.Description);
        Assert.Equal(new byte[] { 9 }, credential.Envelope);
        Assert.Equal(created, credential.CreatedAtUtc);
        Assert.True(credential.UpdatedAtUtc >= created);
    }

    [Fact]
    public void Requires_a_non_empty_envelope()
    {
        Assert.Throws<ArgumentException>(() => new ConnectorCredential(Guid.NewGuid(), WellKnownTenants.DefaultId, "gh", null, null, []));
    }
}
