using Atlas.Application.Assessments;
using Atlas.Domain.Jobs;
using Atlas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Infrastructure.Jobs;

/// <summary>
/// the design notes job queue on the scan_jobs table: a single UPDATE … RETURNING with
/// FOR UPDATE SKIP LOCKED leases the next claimable job atomically across N
/// workers. Expired leases (dead workers) become claimable again automatically.
/// </summary>
public sealed class PostgresScanJobQueue(AtlasDbContext db) : IScanJobQueue
{
    public void Enqueue(ScanJob job) => db.ScanJobs.Add(job);

    public async Task<ScanJob?> ClaimAsync(string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(leaseDuration);

        var claimed = await db.Database.SqlQuery<Guid>($"""
            UPDATE atlas.scan_jobs
            SET state = 'Leased',
                leased_by = {workerId},
                lease_expires_at_utc = {expires},
                attempt = attempt + 1,
                error = NULL
            WHERE id = (
                SELECT id
                FROM atlas.scan_jobs
                WHERE state = 'Queued'
                   OR (state IN ('Leased', 'Running') AND lease_expires_at_utc < {now})
                ORDER BY queued_at_utc
                LIMIT 1
                FOR UPDATE SKIP LOCKED)
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken);

        if (claimed.Count == 0)
        {
            return null;
        }

        var id = claimed[0];
        return await db.ScanJobs.SingleAsync(j => j.Id == id, cancellationToken);
    }

    public async Task<bool> HeartbeatAsync(Guid jobId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var expires = DateTimeOffset.UtcNow.Add(leaseDuration);
        var affected = await db.Database.ExecuteSqlAsync($"""
            UPDATE atlas.scan_jobs
            SET lease_expires_at_utc = {expires}
            WHERE id = {jobId}
              AND leased_by = {workerId}
              AND state IN ('Leased', 'Running')
            """, cancellationToken);
        return affected > 0;
    }

    public Task<bool> HasActiveJobAsync(Guid assessmentId, CancellationToken cancellationToken) =>
        db.ScanJobs.AnyAsync(
            j => j.AssessmentId == assessmentId
                 && (j.State == ScanJobState.Queued || j.State == ScanJobState.Leased || j.State == ScanJobState.Running),
            cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, ScanJobState>> GetActiveJobStatesAsync(
        IReadOnlyCollection<Guid> assessmentIds, CancellationToken cancellationToken)
    {
        var active = await db.ScanJobs
            .Where(j => assessmentIds.Contains(j.AssessmentId)
                        && (j.State == ScanJobState.Queued || j.State == ScanJobState.Leased || j.State == ScanJobState.Running))
            .Select(j => new { j.AssessmentId, j.State, j.QueuedAtUtc })
            .ToListAsync(cancellationToken);

        // Running beats Leased beats Queued when several jobs exist for the same assessment.
        return active
            .GroupBy(j => j.AssessmentId)
            .ToDictionary(g => g.Key, g => g.Max(j => j.State));
    }

    public async Task<IReadOnlyList<ScanJob>> ListRecentAsync(int take, ScanJobState? state, CancellationToken cancellationToken)
    {
        IQueryable<ScanJob> query = db.ScanJobs;
        if (state is { } s)
        {
            query = query.Where(j => j.State == s);
        }

        return await query.OrderByDescending(j => j.QueuedAtUtc).Take(Math.Clamp(take, 1, 500)).ToListAsync(cancellationToken);
    }

    public Task<ScanJob?> GetAsync(Guid jobId, CancellationToken cancellationToken) =>
        db.ScanJobs.SingleOrDefaultAsync(j => j.Id == jobId, cancellationToken);
}
