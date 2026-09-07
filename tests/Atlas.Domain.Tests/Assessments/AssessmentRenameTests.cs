using Atlas.Domain.Assessments;
using Atlas.Domain.Sources;
using Atlas.Domain.Tenants;

namespace Atlas.Domain.Tests.Assessments;

public class AssessmentRenameTests
{
    private static Assessment NewAssessment() =>
        new(Guid.NewGuid(), WellKnownTenants.DefaultId, "Original", new SourceReference(SourceReference.Kinds.Git, "https://example.invalid/r.git", "main", "gh"));

    [Fact]
    public void Rename_trims_and_keeps_everything_else()
    {
        var a = NewAssessment();
        a.Rename("  Renamed  ");
        Assert.Equal("Renamed", a.Name);
        Assert.Equal("gh", a.CredentialName);
        Assert.Equal("gh", a.Source.CredentialName);
        Assert.Equal("main", a.Branch);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rename_rejects_blank(string name) => Assert.Throws<ArgumentException>(() => NewAssessment().Rename(name));
}
