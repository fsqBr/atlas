using Atlas.Domain.Modernization;

namespace Atlas.Application.Assessments;

public interface IModernizationActualRepository
{
    Task<ModernizationActual?> GetAsync(Guid assessmentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ModernizationActual>> ListAllAsync(CancellationToken cancellationToken);

    void Add(ModernizationActual actual);
}

/// <summary>
/// Estimated (cost.v1 likely hours for the strategy actually executed) versus
/// recorded actuals across the estate — the feedback loop that turns the cost
/// model's heuristics into calibrated rates.
/// </summary>
public sealed class CalibrationBuilder(
    IModernizationActualRepository actuals,
    IAssessmentRepository assessments,
    ModernizationPlanBuilder plans)
{
    public async Task<CalibrationSummary> BuildAsync(CancellationToken cancellationToken)
    {
        var recorded = await actuals.ListAllAsync(cancellationToken);
        var points = new List<CalibrationPoint>();
        foreach (var actual in recorded)
        {
            var assessment = await assessments.GetAsync(actual.AssessmentId, cancellationToken);
            var plan = await plans.BuildAsync(actual.AssessmentId, cancellationToken);
            if (assessment is null || plan is null)
            {
                continue;
            }

            // The estimate frozen at record time; recomputing from the current (already modernized)
            // findings would collapse toward the floor and invert the calibration signal.
            var estimated = Math.Max(1, actual.EstimatedHours
                ?? plan.Estimates.First(e => e.Strategy == actual.Strategy).EffortHours.Likely);
            points.Add(new CalibrationPoint(
                actual.AssessmentId, assessment.Name, actual.Strategy, estimated, actual.ActualHours,
                Math.Round(actual.ActualHours / estimated, 2), actual.Notes, actual.RecordedAtUtc));
        }

        return CalibrationSummary.From(points.OrderByDescending(p => p.RecordedAtUtc).ToList());
    }
}
