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

/// <summary>API tokens: machine credentials for CI/scripts; the secret is returned once.</summary>
internal static class TokenEndpoints
{
    public static void Map(WebApplication app)
    {
        // ---- API tokens: machine credentials for CI/scripts (admin-only; the secret is returned once) ----
        var tokensGroup = app.MapGroup("/api/tokens").RequireRateLimiting("api");

        tokensGroup.MapGet("/", async (ApiTokenService service, CancellationToken ct) =>
            Results.Ok((await service.ListAsync(ct)).Select(ToResponse)));

        tokensGroup.MapPost("/", async (HttpContext http, ApiTokenRequest request, ApiTokenService service, CancellationToken ct) =>
        {
            try
            {
                var actor = http.User.Identity?.IsAuthenticated == true
                    ? http.User.FindFirst("preferred_username")?.Value ?? http.User.FindFirst("email")?.Value ?? http.User.FindFirst("name")?.Value ?? http.User.Identity.Name ?? "authenticated"
                    : "anonymous";
                var created = await service.CreateAsync(request.Name, request.Role, actor, request.ExpiresAtUtc, ct);
                return Results.Created($"/api/tokens/{created.Token.Id}", new ApiTokenCreatedResponse(ToResponse(created.Token), created.Secret));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        tokensGroup.MapDelete("/{tokenId:guid}", async (Guid tokenId, ApiTokenService service, CancellationToken ct) =>
            await service.RevokeAsync(tokenId, ct) ? Results.NoContent() : Results.NotFound());

        static ApiTokenResponse ToResponse(Atlas.Application.Security.ApiTokenSummary t) =>
            new(t.Id, t.Name, t.Hint, t.Role, t.CreatedBy, t.CreatedAtUtc, t.ExpiresAtUtc, t.LastUsedAtUtc, t.RevokedAtUtc, t.Active);
    }
}
