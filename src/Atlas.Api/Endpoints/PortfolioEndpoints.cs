using Atlas.Api;
using Atlas.Api.Endpoints;
using Atlas.Application;
using Atlas.Application.Assessments;
using Atlas.Application.Credentials;
using Atlas.Application.Findings;
using Atlas.Connector.Abstractions;
using Atlas.Connector.AzureDevOps;
using Atlas.Connector.Git;
using Atlas.Connector.GitHub;
using Atlas.Connector.GitLab;
using Atlas.Ai;
using Atlas.Application.Ai;
using Atlas.Application.Security;
using Atlas.Application.Tenants;
using Atlas.Connector.Upload;
using Atlas.Language.Abstractions;
using Atlas.Language.CSharp;
using Atlas.Language.Sql;
using Atlas.Language.VisualBasic;
using Atlas.Connector.Local;
using Atlas.Contracts.Assessments;
using Atlas.Domain.Assessments;
using Atlas.Domain.Findings;
using Atlas.Domain.Sources;
using Atlas.Domain.Tenants;
using Atlas.Governance.Infrastructure;
using Atlas.Infrastructure;
using Atlas.Infrastructure.Persistence;
using Atlas.Reporting;
using Atlas.Scanner.Ai;
using Atlas.Scanner.Architecture;
using Atlas.Scanner.Database;
using Atlas.Scanner.JavaScript;
using Atlas.Scanner.Licenses;
using Atlas.Scanner.Dependencies;
using Atlas.Scanner.Infrastructure;
using Atlas.Scanner.Privacy;
using Atlas.Scanner.Quality;
using Atlas.Scanner.Runtime;
using Atlas.Scanner.Secrets;
using Atlas.Scanner.Security;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Api.Endpoints;

/// <summary>Portfolio summary, executive report (HTML/PDF), calibration and the portfolio trend.</summary>
internal static class PortfolioEndpoints
{
    public static void Map(WebApplication app)
    {
        // Estate view: every assessment's latest health, inventory and open findings in one picture.
        app.MapGet("/api/portfolio", async (Atlas.Application.Portfolio.PortfolioBuilder portfolio, CancellationToken ct, string? lang = null, string? tag = null) =>
            Results.Ok(ApiMapping.ToResponse(await portfolio.BuildAsync(lang, ct, tag))))
            .RequireRateLimiting("api");

        // Portfolio executive report: the whole estate (or one product group via ?tag=) as a client-ready document.
        app.MapGet("/api/portfolio/report", async (PortfolioReportBuilder builder, ReportOptions reportOptions, CancellationToken ct, string? lang = null, string? tag = null, int weeks = 26) =>
        {
            var locale = ReportLocale.For(lang);
            var report = await builder.BuildAsync(lang, tag, weeks, ct);
            return report is null
                ? Results.NotFound()
                : Results.Content(PortfolioHtmlRenderer.Render(report, locale, reportOptions), "text/html; charset=utf-8");
        }).RequireRateLimiting("api");

        app.MapGet("/api/portfolio/report.pdf", async (PortfolioReportBuilder builder, IPdfRenderer pdf, ReportOptions reportOptions, ILogger<Program> logger, CancellationToken ct, string? lang = null, string? tag = null, int weeks = 26) =>
        {
            var locale = ReportLocale.For(lang);
            var report = await builder.BuildAsync(lang, tag, weeks, ct);
            if (report is null)
            {
                return Results.NotFound();
            }

            try
            {
                var bytes = await pdf.RenderAsync(PortfolioHtmlRenderer.Render(report, locale, reportOptions), ct, PortfolioHtmlRenderer.RenderPdfFooter(report, locale));
                return Results.File(bytes, "application/pdf", $"atlas-portfolio-{DateTime.UtcNow:yyyyMMdd}.pdf");
            }
            catch (PdfRendererUnavailableException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Portfolio PDF export failed via {Renderer}.", pdf.Description);
                return Results.Json(new { error = $"PDF export failed ({pdf.Description}): {ex.Message}" }, statusCode: StatusCodes.Status502BadGateway);
            }
        }).RequireRateLimiting("api");

        // Estimated vs actual across the estate: is cost.v1 too optimistic or too conservative here?
        app.MapGet("/api/calibration", async (CalibrationBuilder calibration, CancellationToken ct, string? lang = null) =>
            Results.Ok(ApiMapping.ToResponse(await calibration.BuildAsync(ct), lang)));

        app.MapGet("/api/portfolio/trend", async (IAssessmentRunRepository runs, CancellationToken ct, int weeks = 26, string? tag = null) =>
        {
            var points = Atlas.Application.Portfolio.PortfolioTrend.Compute(await runs.ListCompletedPointsAsync(tag, ct), DateOnly.FromDateTime(DateTime.UtcNow), weeks);
            return Results.Ok(points.Select(p => new PortfolioTrendPointResponse(
                p.Date, p.AverageScore, p.OpenFindings, p.Assessed,
                p.DimensionAverages is null ? null : new Dictionary<string, double>(p.DimensionAverages))).ToList());
        }).RequireRateLimiting("api");
    }
}
