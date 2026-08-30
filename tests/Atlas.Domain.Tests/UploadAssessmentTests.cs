using Atlas.Domain.Assessments;
using Atlas.Domain.Sources;
using Atlas.Domain.Tenants;

namespace Atlas.Domain.Tests;

public class UploadAssessmentTests
{
    [Fact]
    public void Upload_assessments_keep_their_repository_key_when_the_archive_is_replaced()
    {
        var id = Guid.NewGuid();
        var assessment = new Assessment(id, WellKnownTenants.DefaultId, "Lettr", new SourceReference(SourceReference.Kinds.Upload, Guid.NewGuid().ToString()));
        var keyBefore = assessment.RepositoryKey;

        var next = Guid.NewGuid();
        assessment.ReplaceUpload(next.ToString("N"));

        Assert.Equal(next.ToString(), assessment.SourceLocator);
        Assert.Equal(keyBefore, assessment.RepositoryKey);
        Assert.Equal($"upload:{id:N}", assessment.RepositoryKey);
    }

    [Fact]
    public void Only_upload_assessments_accept_a_new_upload_and_ids_must_be_guids()
    {
        var local = new Assessment(Guid.NewGuid(), WellKnownTenants.DefaultId, "A", new SourceReference("local", "/sources/a"));
        Assert.Throws<InvalidOperationException>(() => local.ReplaceUpload(Guid.NewGuid().ToString()));
        Assert.Equal("/sources/a", local.RepositoryKey);

        var upload = new Assessment(Guid.NewGuid(), WellKnownTenants.DefaultId, "B", new SourceReference(SourceReference.Kinds.Upload, Guid.NewGuid().ToString()));
        Assert.Throws<ArgumentException>(() => upload.ReplaceUpload("../../etc"));
    }
}
