using System.Text.Json;
using Atlas.Domain.Findings;
using Atlas.Domain.Health;

namespace Atlas.Application.Assessments;

public static class HealthSnapshotFactory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static HealthSnapshot Create(
        Guid tenantId, Guid assessmentId, string? commitSha, IReadOnlyList<Finding> openFindings, int projectCount, Guid? runId = null, DateTimeOffset? createdAtUtc = null)
    {
        var inputs = openFindings.Select(f => new HealthInput(f.RuleId, f.Category, f.Severity)).ToList();
        var result = HealthScoreCalculator.Compute(inputs, projectCount);

        return new HealthSnapshot(
            Guid.NewGuid(), tenantId, assessmentId, commitSha, result, openFindings.Count, projectCount,
            JsonSerializer.Serialize(result.Dimensions, Json), runId, createdAtUtc);
    }

    public static IReadOnlyList<HealthDimension> ReadDimensions(HealthSnapshot snapshot) =>
        JsonSerializer.Deserialize<List<HealthDimension>>(snapshot.DimensionsJson, Json) ?? [];
}
