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

/// <summary>Scan job queue, dead-letter retry, audit trail and the server-sent job event stream.</summary>
internal static class JobEndpoints
{
    public static void Map(WebApplication app)
    {
        // Queue visibility: recent jobs and dead-letter retry.
        app.MapGet("/api/jobs", async (IScanJobQueue queue, IAssessmentRepository repository, CancellationToken ct, string? state = null, int take = 100) =>
        {
            Atlas.Domain.Jobs.ScanJobState? filter = Enum.TryParse<Atlas.Domain.Jobs.ScanJobState>(state, true, out var parsed) ? parsed : null;
            var jobs = await queue.ListRecentAsync(take, filter, ct);
            // The name lookup is ACL-filtered (v0.27 sharing): a job whose assessment the caller cannot see
            // is not listed — errors and lease info can reveal repository details.
            var names = (await repository.ListRecentAsync(500, ct)).ToDictionary(a => a.Id, a => a.Name);
            return Results.Ok(jobs.Where(j => names.ContainsKey(j.AssessmentId)).Select(j => new JobResponse(j.Id, j.AssessmentId, names.GetValueOrDefault(j.AssessmentId), j.Kind, j.State.ToString(), j.Attempt, j.Error, j.QueuedAtUtc, j.StartedAtUtc, j.FinishedAtUtc, j.LeasedBy)));
        });

        app.MapPost("/api/jobs/{jobId:guid}/retry", async (Guid jobId, IScanJobQueue queue, RunAgainHandler runAgain, CancellationToken ct) =>
        {
            var job = await queue.GetAsync(jobId, ct);
            if (job is null)
            {
                return Results.NotFound();
            }

            if (job.State != Atlas.Domain.Jobs.ScanJobState.DeadLetter)
            {
                return Results.Conflict(new { error = "Only dead-letter jobs can be retried." });
            }

            try
            {
                var newJobId = await runAgain.HandleAsync(job.AssessmentId, ct);
                return Results.Accepted($"/api/jobs/{newJobId}", new RunQueuedResponse(newJobId));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        // Append-only audit trail of state-changing API calls (who, what, when, outcome).
        app.MapGet("/api/audit", async (Atlas.Application.Audit.IAuditRepository audit, CancellationToken ct, int take = 200, Guid? assessmentId = null) =>
            Results.Ok((await audit.ListRecentAsync(take, assessmentId, ct)).Select(a => new AuditEntryResponse(a.Id, a.AtUtc, a.Actor, a.Method, a.Path, a.StatusCode, a.AssessmentId, a.Detail))));

        // Live job updates as server-sent events; the UI falls back to polling when unavailable.
        var jobsEventStreams = new int[1];
        app.MapGet("/api/events/jobs", async (HttpContext httpContext, IScanJobQueue queue, IAssessmentRepository repository, CancellationToken ct) =>
        {
            if (Interlocked.Increment(ref jobsEventStreams[0]) > 100)
            {
                Interlocked.Decrement(ref jobsEventStreams[0]);
                httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            try
            {
                httpContext.Response.Headers.ContentType = "text/event-stream";
                httpContext.Response.Headers.CacheControl = "no-cache";
                httpContext.Response.Headers["X-Accel-Buffering"] = "no"; // nginx: never buffer this response
                var serializerOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
                var last = "";
                var beat = 0;
                Dictionary<Guid, string> names = [];
                while (!ct.IsCancellationRequested)
                {
                    if (beat % 15 == 0)
                    {
                        // The name lookup is ACL-filtered (v0.27 sharing): it doubles as the visibility gate
                        // below, and refreshing it every 30s keeps the per-connection query cost low.
                        names = (await repository.ListRecentAsync(500, ct)).ToDictionary(a => a.Id, a => a.Name);
                    }

                    var jobs = await queue.ListRecentAsync(100, null, ct);
                    var payload = System.Text.Json.JsonSerializer.Serialize(
                        jobs.Where(j => names.ContainsKey(j.AssessmentId))
                            .Select(j => new JobResponse(j.Id, j.AssessmentId, names.GetValueOrDefault(j.AssessmentId), j.Kind, j.State.ToString(), j.Attempt, j.Error, j.QueuedAtUtc, j.StartedAtUtc, j.FinishedAtUtc, j.LeasedBy)),
                        serializerOptions);
                    if (payload != last)
                    {
                        await httpContext.Response.WriteAsync("data: " + payload + "\n\n", ct);
                        await httpContext.Response.Body.FlushAsync(ct);
                        last = payload;
                    }
                    else if (beat % 8 == 7)
                    {
                        await httpContext.Response.WriteAsync(": ping\n\n", ct);
                        await httpContext.Response.Body.FlushAsync(ct);
                    }

                    beat++;
                    await Task.Delay(2000, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // client went away
            }
            finally
            {
                Interlocked.Decrement(ref jobsEventStreams[0]);
            }
        }).RequireRateLimiting("api");
    }
}
