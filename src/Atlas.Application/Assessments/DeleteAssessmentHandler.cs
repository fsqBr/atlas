using Microsoft.Extensions.Logging;

namespace Atlas.Application.Assessments;

public sealed class AssessmentBusyException(Guid assessmentId)
    : InvalidOperationException($"Assessment {assessmentId} has a queued or running job; wait for it to finish before deleting.")
{
    public Guid AssessmentId { get; } = assessmentId;
}

/// <summary>
/// Deletes an assessment with everything derived from it (runs, scans, findings,
/// occurrences, suppressions, inventory and health snapshots, jobs) through the
/// database's cascading foreign keys. Refused while a job is queued or running:
/// a worker would otherwise write into rows that no longer exist.
/// </summary>
public sealed class DeleteAssessmentHandler(
    IAssessmentRepository assessments,
    IScanJobQueue jobs,
    IUnitOfWork unitOfWork,
    ILogger<DeleteAssessmentHandler> logger)
{
    public async Task<bool> HandleAsync(Guid assessmentId, CancellationToken cancellationToken)
    {
        var assessment = await assessments.GetAsync(assessmentId, cancellationToken);
        if (assessment is null)
        {
            return false;
        }

        if (await jobs.HasActiveJobAsync(assessmentId, cancellationToken))
        {
            throw new AssessmentBusyException(assessmentId);
        }

        assessments.Remove(assessment);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Assessment {AssessmentId} ({Name}) deleted.", assessmentId, assessment.Name);
        return true;
    }
}
