using Atlas.Application.Assessments;
using Atlas.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace Atlas.Infrastructure.Persistence;

public sealed class SystemMarkerRepository(AtlasDbContext db) : ISystemMarkerRepository
{
    public Task<SystemMarker?> GetAsync(string key, CancellationToken cancellationToken) =>
        db.SystemMarkers.SingleOrDefaultAsync(m => m.Key == key, cancellationToken);

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        var marker = await GetAsync(key, cancellationToken);
        if (marker is null)
        {
            db.SystemMarkers.Add(new SystemMarker(key, value));
        }
        else
        {
            marker.Set(value);
        }
    }

    public async Task<bool> TryClaimAsync(string key, string value, DateTimeOffset staleBefore, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        // ON CONFLICT DO UPDATE ... WHERE stale: a fresh marker leaves the row untouched and returns
        // nothing, so only the replica that inserts or refreshes a stale row claims the send.
        var claimed = await db.Database.SqlQuery<string>($"""
            INSERT INTO atlas.system_markers (key, value, updated_at_utc)
            VALUES ({key}, {value}, {now})
            ON CONFLICT (key) DO UPDATE
                SET value = EXCLUDED.value, updated_at_utc = EXCLUDED.updated_at_utc
                WHERE atlas.system_markers.updated_at_utc < {staleBefore}
            RETURNING key AS "Value"
            """).ToListAsync(cancellationToken);
        return claimed.Count > 0;
    }
}
