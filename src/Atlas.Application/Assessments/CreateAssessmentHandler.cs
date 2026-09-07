using Atlas.Application.Credentials;
using Atlas.Connector.Abstractions;
using Atlas.Domain.Assessments;
using Atlas.Domain.Jobs;
using Atlas.Domain.Sources;
using Atlas.Application.Tenants;

namespace Atlas.Application.Assessments;

public sealed record CreateAssessmentResult(Guid AssessmentId, Guid JobId);

/// <summary>
/// Creates an assessment and queues its job. Scans never run inside the HTTP
/// request; the worker claims the job asynchronously.
/// </summary>
public sealed class CreateAssessmentHandler(
    IAssessmentRepository assessments,
    IScanJobQueue jobs,
    IUnitOfWork unitOfWork,
    IEnumerable<ISourceConnector> connectors,
    ICredentialRepository credentials,
    ITenantContext tenant)
{
    public async Task<CreateAssessmentResult> HandleAsync(
        string name,
        SourceReference source,
        CancellationToken cancellationToken,
        IEnumerable<string>? excludeGlobs = null)
    {
        if (!connectors.Any(c => c.CanHandle(source)))
        {
            throw new ArgumentException(
                $"No connector can handle source kind '{source.Kind}'.", nameof(source));
        }

        if (source.Locator.StartsWith(DemoSeeder.LocatorPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The demo:// locator prefix is reserved for the demo estate.", nameof(source));
        }

        if (!string.IsNullOrWhiteSpace(source.CredentialName)
            && await credentials.GetByNameAsync(tenant.Require(), source.CredentialName, cancellationToken) is null)
        {
            throw new ArgumentException($"Credential '{source.CredentialName}' does not exist.", nameof(source));
        }

        var assessment = new Assessment(Guid.NewGuid(), tenant.Require(), name, source);
        assessment.SetExcludeGlobs(excludeGlobs);
        var job = new ScanJob(Guid.NewGuid(), assessment.TenantId, assessment.Id);

        assessments.Add(assessment);
        jobs.Enqueue(job);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new CreateAssessmentResult(assessment.Id, job.Id);
    }
}
