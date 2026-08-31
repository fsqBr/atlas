using Atlas.Application.Assessments;
using Atlas.Domain.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.IntegrationTests;

public sealed class AssessmentLifecycleTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Rename_then_delete_refused_while_a_job_is_active_and_cascades_afterwards()
    {
        await using var provider = fixture.BuildServices();

        Guid assessmentId;
        using (var scope = provider.CreateScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<CreateAssessmentHandler>().HandleAsync(
                "Old name", new SourceReference(SourceReference.Kinds.LocalFolder, fixture.WorkspaceRoot), CancellationToken.None);
            assessmentId = created.AssessmentId;
        }

        // Rename is a plain aggregate operation persisted by the unit of work.
        using (var scope = provider.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IAssessmentRepository>();
            var assessment = await repository.GetAsync(assessmentId, CancellationToken.None);
            assessment!.Rename("  New name ");
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(CancellationToken.None);
        }

        using (var scope = provider.CreateScope())
        {
            var assessment = await scope.ServiceProvider.GetRequiredService<IAssessmentRepository>().GetAsync(assessmentId, CancellationToken.None);
            Assert.Equal("New name", assessment!.Name);

            // The job is still queued: deletion must be refused.
            var handler = scope.ServiceProvider.GetRequiredService<DeleteAssessmentHandler>();
            await Assert.ThrowsAsync<AssessmentBusyException>(() => handler.HandleAsync(assessmentId, CancellationToken.None));
        }

        // Finish the job like a worker would.
        using (var scope = provider.CreateScope())
        {
            var job = await scope.ServiceProvider.GetRequiredService<IScanJobQueue>().ClaimAsync("test-worker", TimeSpan.FromMinutes(1), CancellationToken.None);
            Assert.NotNull(job);
            Assert.Equal(assessmentId, job!.AssessmentId);
            job.Start();
            job.Succeed();
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(CancellationToken.None);
        }

        using (var scope = provider.CreateScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<DeleteAssessmentHandler>();
            Assert.True(await handler.HandleAsync(assessmentId, CancellationToken.None));
            Assert.False(await handler.HandleAsync(assessmentId, CancellationToken.None));
        }

        using (var scope = provider.CreateScope())
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<IAssessmentRepository>().GetAsync(assessmentId, CancellationToken.None));
            Assert.False(await scope.ServiceProvider.GetRequiredService<IScanJobQueue>().HasActiveJobAsync(assessmentId, CancellationToken.None));
        }
    }
}
