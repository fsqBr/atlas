using Atlas.Application.Assessments;
using Atlas.Domain.Findings;

namespace Atlas.Application.Findings;

public sealed record RuleGroupView(string RuleId, string Title, FindingCategory Category, Severity MaxSeverity, int Count, IReadOnlyList<string> SampleFiles);

public sealed record HeatmapRow(string Folder, int Open, int Critical, int High, int Medium, int Low, int Informational, int Files);

/// <summary>
/// Two aggregate views over open findings: grouped by rule (what) and by
/// folder (where the debt lives — a heatmap). Both read persisted findings only.
/// </summary>
public sealed class FindingViewsBuilder(IFindingRepository findings, IRuleCatalog rules)
{
    private const int MaxFindings = 20_000;

    public async Task<IReadOnlyList<RuleGroupView>> ByRuleAsync(Guid assessmentId, string? lang, CancellationToken cancellationToken)
    {
        var items = await OpenAsync(assessmentId, cancellationToken);
        var catalog = await rules.GetAllAsync(cancellationToken);
        return items
            .GroupBy(i => i.Finding.RuleId, StringComparer.Ordinal)
            .Select(g => new RuleGroupView(
                g.Key,
                FindingLocalizer.RuleTitle(catalog.GetValueOrDefault(g.Key), g.Key, lang),
                g.First().Finding.Category,
                g.Max(i => i.Finding.Severity),
                g.Count(),
                g.Select(i => i.Latest?.Evidence.FilePath).Where(p => p is not null).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList()))
            .OrderByDescending(r => r.MaxSeverity).ThenByDescending(r => r.Count).ThenBy(r => r.RuleId, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<HeatmapRow>> HeatmapAsync(Guid assessmentId, int depth, CancellationToken cancellationToken)
    {
        var items = await OpenAsync(assessmentId, cancellationToken);
        return Heatmap(items.Select(i => (i.Latest?.Evidence.FilePath, i.Finding.Severity)), depth);
    }

    /// <summary>Pure aggregation: folder (first `depth` segments) → counts. Findings without a path land in "(no file)".</summary>
    public static IReadOnlyList<HeatmapRow> Heatmap(IEnumerable<(string? FilePath, Severity Severity)> findings, int depth = 2)
    {
        var rows = new Dictionary<string, (int[] BySeverity, HashSet<string> Files)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, severity) in findings)
        {
            var folder = Folder(path, depth);
            if (!rows.TryGetValue(folder, out var row))
            {
                rows[folder] = row = (new int[5], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }

            row.BySeverity[(int)severity]++;
            if (path is not null)
            {
                row.Files.Add(path);
            }
        }

        return rows
            .Select(kv => new HeatmapRow(kv.Key, kv.Value.BySeverity.Sum(),
                kv.Value.BySeverity[(int)Severity.Critical], kv.Value.BySeverity[(int)Severity.High], kv.Value.BySeverity[(int)Severity.Medium],
                kv.Value.BySeverity[(int)Severity.Low], kv.Value.BySeverity[(int)Severity.Informational], kv.Value.Files.Count))
            .OrderByDescending(r => r.Critical * 15 + r.High * 8 + r.Medium * 3 + r.Low)
            .ThenBy(r => r.Folder, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Folder(string? path, int depth)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "(no file)";
        }

        var normalized = path.Replace('\\', '/').TrimStart('.', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= 1)
        {
            return "(root)";
        }

        return string.Join('/', segments.Take(Math.Min(depth, segments.Length - 1)));
    }

    private async Task<List<FindingWithLatestOccurrence>> OpenAsync(Guid assessmentId, CancellationToken cancellationToken)
    {
        var open = await findings.ListAsync(assessmentId, 0, MaxFindings, cancellationToken, new FindingFilter(Status: FindingStatus.Open));
        var regressed = await findings.ListAsync(assessmentId, 0, MaxFindings, cancellationToken, new FindingFilter(Status: FindingStatus.Regressed));
        return open.Items.Concat(regressed.Items).ToList();
    }
}
