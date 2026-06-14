using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Services.Trip;

/// <summary>
/// TRIP-INVALIDATE-01 (Story 2.4): default <see cref="IRouteSegmentInvalidationService"/>.
/// Each call takes a fresh per-worker <see cref="AppDbContext"/> from the factory
/// (EF contexts are not thread-safe) and commits the delete under the shared
/// process-wide <see cref="SqliteWriteLock"/>, the same write gate the enrichment
/// worker, dedup pass and compute service use.
/// </summary>
public sealed class RouteSegmentInvalidationService(
    IDbContextFactory<AppDbContext> factory,
    SqliteWriteLock writeLock,
    ILogger<RouteSegmentInvalidationService> logger) : IRouteSegmentInvalidationService
{
    /// <inheritdoc />
    public async Task InvalidateForPoiAsync(int poiId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        // Both directions, all modes — but never a Manual row (user-entered, not
        // derived from coordinates).
        var stale = await db.RouteSegments
            .Where(r => (r.FromPoiId == poiId || r.ToPoiId == poiId)
                        && r.Fidelity != Fidelity.Manual)
            .ToListAsync(ct);

        if (stale.Count == 0)
        {
            return;
        }

        db.RouteSegments.RemoveRange(stale);
        await SaveUnderWriteLockAsync(db, ct);

        logger.LogInformation(
            "TRIP-INVALIDATE-01: invalidated {Count} cached segment(s) touching POI {PoiId} (coords changed)",
            stale.Count, poiId);
    }

    /// <inheritdoc />
    public async Task<int> InvalidateRecomputableForCollectionAsync(int collectionId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var poiIds = await db.PoiCollectionItems
            .AsNoTracking()
            .Where(ci => ci.PoiCollectionId == collectionId)
            .Select(ci => ci.PoiId)
            .ToListAsync(ct);

        if (poiIds.Count == 0)
        {
            return 0;
        }

        // The collection's current legs run between its own POIs. Eligible = NOT
        // Manual AND NOT Measured (so Estimated/Placeholder/EstimatedFallback are
        // deleted; a user's Manual time and any higher-fidelity Measured row survive
        // — the latter is never silently downgraded).
        var eligible = await db.RouteSegments
            .Where(r => poiIds.Contains(r.FromPoiId)
                        && poiIds.Contains(r.ToPoiId)
                        && r.Fidelity != Fidelity.Manual
                        && r.Fidelity != Fidelity.Measured)
            .ToListAsync(ct);

        if (eligible.Count == 0)
        {
            return 0;
        }

        db.RouteSegments.RemoveRange(eligible);
        await SaveUnderWriteLockAsync(db, ct);

        logger.LogInformation(
            "TRIP-INVALIDATE-01: invalidated {Count} recomputable segment(s) for collection {CollectionId} (explicit recompute)",
            eligible.Count, collectionId);

        return eligible.Count;
    }

    private async Task SaveUnderWriteLockAsync(AppDbContext db, CancellationToken ct)
    {
        await writeLock.Gate.WaitAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            writeLock.Gate.Release();
        }
    }
}
