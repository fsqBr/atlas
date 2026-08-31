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
}
