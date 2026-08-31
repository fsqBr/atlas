using Atlas.Domain.Jobs;

namespace Atlas.Application.Assessments;

/// <summary>Queues a new run of an existing assessment; refuses while one is already queued or running.</summary>
public sealed class RunAgainHandler(
    IAssessmentRepository assessments,
    IScanJobQueue jobs,
    IUnitOfWork unitOfWork)
{
    public async Task<Guid> HandleAsync(Guid assessmentId, CancellationToken cancellationToken)
    {
        var assessment = await assessments.GetAsync(assessmentId, cancellationToken)
            ?? throw new KeyNotFoundException($"Assessment {assessmentId} not found.");

        if (await jobs.HasActiveJobAsync(assessmentId, cancellationToken))
        {
            throw new InvalidOperationException("A run is already queued or in progress for this assessment.");
        }

        var job = new ScanJob(Guid.NewGuid(), assessment.TenantId, assessment.Id);
        jobs.Enqueue(job);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return job.Id;
    }
}
