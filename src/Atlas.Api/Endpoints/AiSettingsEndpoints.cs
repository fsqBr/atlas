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

/// <summary>AI provider settings, connectivity test, feedback summary and cost estimate.</summary>
internal static class AiSettingsEndpoints
{
    public static void Map(WebApplication app)
    {
        var aiGroup = app.MapGroup("/api/ai").RequireRateLimiting("api");

        aiGroup.MapGet("/settings", async (AiSettingsService service, CancellationToken ct) => Results.Ok(await service.GetAsync(ct)));

        aiGroup.MapPut("/settings", async (AiSettingsRequest request, AiSettingsService service, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await service.UpsertAsync(request.Provider, request.Model, request.BaseUrl, request.ApiKey, request.Enabled, request.MaxSnippetsPerAnalysis, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (SecretStoreNotConfiguredException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        aiGroup.MapDelete("/settings/key", async (AiSettingsService service, CancellationToken ct) => Results.Ok(await service.ClearKeyAsync(ct)));

        aiGroup.MapPost("/test", async (AiSettingsService service, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await service.TestAsync(ct));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });


        // Perceived quality of the AI features: votes per kind and per model, latest comments.
        aiGroup.MapGet("/feedback", async (AiFeedbackService service, CancellationToken ct) =>
        {
            var s = await service.SummarizeAsync(ct);
            return Results.Ok(new AiFeedbackSummaryResponse(s.Up, s.Down,
                s.ByKind.Select(b => new FeedbackBucketResponse(b.Key, b.Up, b.Down, b.HelpfulShare)).ToList(),
                s.ByModel.Select(b => new FeedbackBucketResponse(b.Key, b.Up, b.Down, b.HelpfulShare)).ToList(),
                s.Recent.Select(e => new FeedbackEntryResponse(e.Kind, e.Model, e.Rating, e.Comment, e.AssessmentId, e.RatedBy, e.RatedAtUtc, e.Title)).ToList()));
        });

        // Pre-flight cost of one business-rule analysis at the configured cap (no materialization).
        aiGroup.MapGet("/estimate", async (AiSettingsService service, CancellationToken ct, int? methods = null) =>
        {
            var settings = await service.GetAsync(ct);
            var e = AiNarrativeService.Estimate(methods ?? settings.MaxSnippetsPerAnalysis, BusinessRuleExtractor.SnippetsPerBatch);
            return Results.Ok(new AiEstimateResponse(e.Methods, e.Requests, e.InputTokens, e.OutputTokens, e.Note));
        });
    }
}
