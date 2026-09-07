using Atlas.Application.Assessments;
using Atlas.Application.Findings;
using Atlas.Contracts.Assessments;

namespace Atlas.Api.Endpoints;

/// <summary>
/// Waivers and standing suppression policies: tenant-wide policies, assessment-scoped
/// policies, the waiver audit list, and waiver migration after a rule major bump.
/// First endpoint module extracted from Program.cs; new groups follow this shape.
/// </summary>
internal static class SuppressionEndpoints
{
    public static void Map(WebApplication app, RouteGroupBuilder assessments)
    {
        // Tenant-wide suppression policies.
        app.MapGet("/api/policies", async (ISuppressionPolicyRepository repository, CancellationToken ct) =>
            Results.Ok((await repository.ListAllAsync(ct)).Select(ApiMapping.ToResponse)));

        app.MapPost("/api/policies", async (CreatePolicyRequest request, SuppressionPolicyHandler handler, CancellationToken ct) =>
        {
            try
            {
                var (policy, _) = await handler.CreateAsync(null, request.RulePattern, request.PathGlob, request.Reason, request.Author, request.ExpiresAtUtc, ct);
                return Results.Created($"/api/policies/{policy.Id}", ApiMapping.ToResponse(policy));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapDelete("/api/policies/{policyId:guid}", async (Guid policyId, SuppressionPolicyHandler handler, CancellationToken ct) =>
            await handler.DeleteAsync(policyId, ct) ? Results.NoContent() : Results.NotFound());

        // Suppression policies: standing "this rule / this path is noise here" decisions (assessment-scoped here).
        assessments.MapGet("/{id:guid}/policies", async (Guid id, ISuppressionPolicyRepository repository, CancellationToken ct) =>
            Results.Ok((await repository.ListForAssessmentAsync(id, ct)).Select(ApiMapping.ToResponse)));

        assessments.MapPost("/{id:guid}/policies", async (Guid id, CreatePolicyRequest request, IAssessmentRepository repository, SuppressionPolicyHandler handler, CancellationToken ct) =>
        {
            if (await repository.GetAsync(id, ct) is null)
            {
                return Results.NotFound();
            }

            try
            {
                var (policy, applied) = await handler.CreateAsync(id, request.RulePattern, request.PathGlob, request.Reason, request.Author, request.ExpiresAtUtc, ct);
                return Results.Created($"/api/policies/{policy.Id}", new PolicyCreatedResponse(ApiMapping.ToResponse(policy), applied));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        assessments.MapGet("/{id:guid}/suppressions", async (Guid id, ISuppressionRepository suppressions, CancellationToken ct) =>
            Results.Ok((await suppressions.ListByAssessmentAsync(id, ct)).Select(s => new
            {
                s.Id, s.FindingId, s.Fingerprint, Kind = s.Kind.ToString(), s.Reason, s.Author, s.CreatedAtUtc, s.ExpiresAtUtc, s.RevokedAtUtc, s.RevokedBy,
            })));

        // Waivers orphaned by a rule major bump: the superseded finding kept its waiver, the
        // successor starts Open. Migration is an explicit human decision, never automatic.
        assessments.MapGet("/{id:guid}/suppressions/migratable", async (Guid id, WaiverMigrationHandler handler, CancellationToken ct) =>
        {
            var items = await handler.ListAsync(id, ct);
            return items is null
                ? Results.NotFound()
                : Results.Ok(items.Select(m => new MigratableWaiverResponse(
                    m.Finding.Id, m.Finding.RuleId, m.Finding.Title, m.Finding.Severity.ToString(),
                    m.Finding.PredecessorFingerprint!,
                    m.Predecessor.Kind.ToString(), m.Predecessor.Reason, m.Predecessor.Author,
                    m.Predecessor.CreatedAtUtc, m.Predecessor.ExpiresAtUtc)));
        });

        assessments.MapPost("/{id:guid}/suppressions/migrate", async (Guid id, MigrateWaiversRequest request, WaiverMigrationHandler handler, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Author))
            {
                return Results.BadRequest(new { error = "author is required: a migrated waiver is a new auditable decision." });
            }

            var result = await handler.MigrateAsync(id, request.FindingIds, request.Author, ct);
            return result is null
                ? Results.NotFound()
                : Results.Ok(new WaiverMigrationResponse(result.Migrated, result.Skipped, result.MigratedFindingIds));
        });
    }
}
