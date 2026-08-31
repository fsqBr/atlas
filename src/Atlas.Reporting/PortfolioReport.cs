using Atlas.Application.Assessments;
using Atlas.Application.Portfolio;

namespace Atlas.Reporting;

/// <summary>
/// The estate (or one product group) as a client-ready document: the portfolio summary that the
/// dashboard shows, plus the weekly health trend. Everything is recomputed from persisted
/// snapshots — no re-analysis, deterministic for the same data.
/// </summary>
public sealed record PortfolioReport(
    string BrandName,
    string? PreparedBy,
    string? Tag,
    DateTimeOffset GeneratedAtUtc,
    PortfolioSummary Summary,
    IReadOnlyList<PortfolioTrendPoint> Trend);

public sealed class PortfolioReportBuilder(
    PortfolioBuilder portfolio,
    IAssessmentRunRepository runs,
    ReportOptions options)
{
    public async Task<PortfolioReport?> BuildAsync(string? lang, string? tag, int weeks, CancellationToken cancellationToken)
    {
        var summary = await portfolio.BuildAsync(lang, cancellationToken, tag);
        if (summary.Assessments == 0)
        {
            return null;
        }

        var points = PortfolioTrend.Compute(
            await runs.ListCompletedPointsAsync(string.IsNullOrWhiteSpace(tag) ? null : tag.Trim(), cancellationToken),
            DateOnly.FromDateTime(DateTime.UtcNow),
            weeks);

        return new PortfolioReport(
            options.BrandName,
            options.PreparedBy,
            string.IsNullOrWhiteSpace(tag) ? null : tag.Trim(),
            DateTimeOffset.UtcNow,
            summary,
            points);
    }
}
