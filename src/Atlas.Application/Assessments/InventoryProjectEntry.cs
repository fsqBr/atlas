using System.Text.Json;
using Atlas.Domain.Assessments;
using Atlas.Domain.Workspaces;
using Atlas.Language.Abstractions;

namespace Atlas.Application.Assessments;

/// <summary>Per-project row stored inside InventorySnapshot.ProjectsJson.</summary>
public sealed record InventoryProjectEntry(
    string Path,
    string Name,
    bool IsSdkStyle,
    string? TargetFramework,
    int PackageCount,
    int PackagesConfigCount,
    int ProjectReferenceCount,
    string? UiFramework = null);

public static class InventorySnapshotFactory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static InventorySnapshot FromLanguage(
        Guid tenantId, Assessment assessment, Workspace workspace, LanguageAnalysisResult result, Guid? runId = null)
    {
        var projects = result.Projects
            .Select(p => new InventoryProjectEntry(
                p.RelativePath,
                p.Name,
                p.IsSdkStyle,
                p.TargetFramework,
                p.PackageReferences.Count,
                p.PackageReferences.Count(r => r.Origin == PackageReferenceOrigin.PackagesConfig),
                p.ProjectReferences.Count,
                p.UiFramework))
            .OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new InventorySnapshot(
            Guid.NewGuid(),
            tenantId,
            assessment.Id,
            workspace.Id,
            workspace.CommitSha,
            result.LanguageId,
            result.TierAchieved.ToString(),
            result.Totals.FileCount,
            result.Totals.TotalLines,
            result.Totals.TypeCount,
            result.Totals.MethodCount,
            result.Totals.MaxCyclomaticComplexity,
            result.Totals.AverageCyclomaticComplexity,
            result.Symbols?.ResolutionRate,
            result.Projects.Count,
            result.Solutions.Count,
            JsonSerializer.Serialize(projects, Json),
            runId);
    }

    public static IReadOnlyList<InventoryProjectEntry> ReadProjects(InventorySnapshot snapshot) =>
        JsonSerializer.Deserialize<List<InventoryProjectEntry>>(snapshot.ProjectsJson, Json) ?? [];
}
