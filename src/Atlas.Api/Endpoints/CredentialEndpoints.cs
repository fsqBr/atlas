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

/// <summary>Connector credentials: write-only secrets referenced by name (admin).</summary>
internal static class CredentialEndpoints
{
    public static void Map(WebApplication app)
    {
        // Connector credentials for private sources. Write-only: PUT stores/rotates, GET lists metadata, the secret never leaves.
        var credentialsGroup = app.MapGroup("/api/credentials").RequireRateLimiting("api");

        credentialsGroup.MapGet("/", async (CredentialsService service, ISecretCipher cipher, CancellationToken ct) =>
            Results.Ok(new
            {
                configured = cipher.IsConfigured,
                items = (await service.ListAsync(ct)).Select(ApiMapping.ToResponse),
            }));

        credentialsGroup.MapPut("/{name}", async (string name, UpsertCredentialRequest request, CredentialsService service, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Secret))
            {
                return Results.BadRequest(new { error = "secret is required." });
            }

            try
            {
                var summary = await service.UpsertAsync(name, request.Username, request.Secret, request.Description, ct);
                return Results.Ok(ApiMapping.ToResponse(summary));
            }
            catch (SecretStoreNotConfiguredException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        credentialsGroup.MapDelete("/{name}", async (string name, CredentialsService service, CancellationToken ct) =>
        {
            try
            {
                return await service.DeleteAsync(name, ct) ? Results.NoContent() : Results.NotFound();
            }
            catch (CredentialInUseException ex)
            {
                return Results.Conflict(new { error = ex.Message, assessments = ex.Assessments });
            }
        });
    }
}
