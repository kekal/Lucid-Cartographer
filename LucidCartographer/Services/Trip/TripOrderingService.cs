using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Services.Trip;

/// <summary>
/// Sole writer of <see cref="PoiCollectionItem.OrderIndex"/>. Every order
/// change loads the collection's membership rows tracked, computes the desired
/// 1-based contiguous arrangement, and commits through the one
/// <see cref="SetOrderAsync"/> method under the shared <see cref="SqliteWriteLock"/>
/// so a concurrent enrichment / dedup write never collides.
/// </summary>
public sealed class TripOrderingService(
    IDbContextFactory<AppDbContext> factory,
    SqliteWriteLock writeLock,
    ILogger<TripOrderingService> logger) : ITripOrderingService
{
    public async Task<bool> HasOrderAsync(int collectionId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // The null-coordinate check is inlined here (and in GetStopOrderAsync)
        // only because EF must translate it to SQL; the rule is the canonical
        // StopPlaceability predicate ([TRIP-PLACE-01]) — keep them in lockstep.
        // Defensive: only a placeable item counts as "ordered". Guards against a
        // backfill/migration that numbered non-placeable rows — otherwise such
        // rows would make a never-properly-seeded collection report as ordered
        // and skip the seed path. [Review][Patch]
        return await db.PoiCollectionItems
            .AnyAsync(ci => ci.PoiCollectionId == collectionId && ci.OrderIndex > 0
                && ci.Poi.Latitude != null && ci.Poi.Longitude != null, ct);
    }

    public async Task<IReadOnlyDictionary<int, int>> GetStopOrderAsync(int collectionId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.PoiCollectionItems
            .AsNoTracking()
            // Only placeable, ordered items are Stops — a non-placeable row with a
            // stray OrderIndex (e.g. from a backfill) must never surface as a badge.
            // [Review][Patch]
            .Where(ci => ci.PoiCollectionId == collectionId && ci.OrderIndex > 0
                && ci.Poi.Latitude != null && ci.Poi.Longitude != null)
            .Select(ci => new { ci.PoiId, ci.OrderIndex })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.PoiId, r => r.OrderIndex);
    }

    public async Task<IReadOnlyList<PlaceableStop>> GetPlaceableStopsAsync(int collectionId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.PoiCollectionItems
            .AsNoTracking()
            .Where(ci => ci.PoiCollectionId == collectionId && ci.OrderIndex > 0)
            .OrderBy(ci => ci.OrderIndex)
            .Select(ci => new { ci.PoiId, ci.OrderIndex, ci.Poi.Latitude, ci.Poi.Longitude })
            .ToListAsync(ct);

        // [TRIP-PLACE-03] The routing candidate set is the placeable subset only,
        // filtered through the one canonical predicate. Unplaceable stops (null
        // lat OR null lon) never enter any all-pairs computation.
        return rows
            .Where(r => StopPlaceability.IsPlaceable(r.Latitude, r.Longitude))
            .Select(r => new PlaceableStop(r.PoiId, r.OrderIndex, r.Latitude!.Value, r.Longitude!.Value))
            .ToList();
    }

    public async Task SeedOrderAsync(int collectionId, CancellationToken ct = default)
    {
        var rows = await ReadAsync(collectionId, ct);

        // TRIP-ORDER-01: 1-based contiguous seed by AddedDate asc, tie-broken by
        // PoiId asc. Only placeable items become Stops; non-placeable reset to 0.
        var desired = new Dictionary<int, int>(rows.Count);
        var index = 1;
        foreach (var row in rows.Where(r => r.Placeable)
                     .OrderBy(r => r.AddedDate)
                     .ThenBy(r => r.PoiId))
        {
            desired[row.PoiId] = index++;
        }
        foreach (var row in rows.Where(r => !r.Placeable))
        {
            desired[row.PoiId] = 0;
        }

        await SetOrderAsync(collectionId, desired, ct);
    }

    public async Task AppendStopAsync(int collectionId, int poiId, CancellationToken ct = default)
    {
        var rows = await ReadAsync(collectionId, ct);
        var target = rows.FirstOrDefault(r => r.PoiId == poiId);
        if (target is null || !target.Placeable || target.Order > 0)
        {
            // Absent, not placeable, or already a Stop — nothing to append.
            return;
        }

        var max = rows.Count == 0 ? 0 : rows.Max(r => r.Order);
        await SetOrderAsync(collectionId, new Dictionary<int, int> { [poiId] = max + 1 }, ct);
    }

    public async Task CompactOrderAsync(int collectionId, CancellationToken ct = default)
    {
        var rows = await ReadAsync(collectionId, ct);
        var desired = Renumber(rows.Where(r => r.Placeable && r.Order > 0)
            .OrderBy(r => r.Order)
            .ThenBy(r => r.AddedDate)
            .ThenBy(r => r.PoiId));
        await SetOrderAsync(collectionId, desired, ct);
    }

    public async Task ReconcileOrderAsync(int collectionId, CancellationToken ct = default)
    {
        var rows = await ReadAsync(collectionId, ct);

        // Existing Stops first (by current order), then any placeable item that
        // has no order yet appended in AddedDate/PoiId order — i.e. new additions
        // land at the end. Then renumber the whole sequence 1..N (closing any gap
        // a removal left). Non-placeable items are left untouched (stay 0).
        var ordered = rows.Where(r => r.Placeable && r.Order > 0)
            .OrderBy(r => r.Order)
            .ThenBy(r => r.AddedDate)
            .ThenBy(r => r.PoiId);
        var appended = rows.Where(r => r.Placeable && r.Order == 0)
            .OrderBy(r => r.AddedDate)
            .ThenBy(r => r.PoiId);

        await SetOrderAsync(collectionId, Renumber(ordered.Concat(appended)), ct);
    }

    public async Task ReorderStopAsync(int collectionId, int poiId, int targetOrderIndex, CancellationToken ct = default)
    {
        var rows = await ReadAsync(collectionId, ct);

        // TRIP-ORDER-02 (AR-11 single writer): drag and keyboard both land here
        // and funnel through the same Renumber + SetOrderAsync the seed/compaction
        // paths use — no second renumbering routine exists. The current sequence
        // is the compacted Stop list (placeable, ordered); the move is a single
        // remove + insert in that sequence followed by a full 1..N renumber, so
        // the result is contiguous, gap-free and unique by construction.
        var stops = rows.Where(r => r.Placeable && r.Order > 0)
            .OrderBy(r => r.Order)
            .ThenBy(r => r.AddedDate)
            .ThenBy(r => r.PoiId)
            .ToList();

        var currentIndex = stops.FindIndex(s => s.PoiId == poiId);
        if (currentIndex < 0)
        {
            // Not a Stop of this collection (absent, non-placeable or unordered).
            return;
        }

        // Pin enforcement: a designated Start is fixed at Order 1, a designated
        // Finish at Order N. The movable window is [2..N-1] when both are pinned,
        // [2..N] Start-only, [1..N-1] Finish-only, [1..N] when neither. Pins only
        // count when the designated POI actually is a Stop here (defensive).
        // Reordering NEVER touches StartPoiId/FinishPoiId — that is Story 1.7.
        var (startPoiId, finishPoiId) = await ReadPinsAsync(collectionId, ct);
        var startPinned = startPoiId is { } sid && stops.Any(s => s.PoiId == sid);
        var finishPinned = finishPoiId is { } fid && stops.Any(s => s.PoiId == fid);

        if ((startPinned && poiId == startPoiId) || (finishPinned && poiId == finishPoiId))
        {
            // The pinned Start/Finish itself never moves.
            return;
        }

        var n = stops.Count;
        var min = startPinned ? 2 : 1;
        var max = finishPinned ? n - 1 : n;
        if (min > max)
        {
            // No movable interior slot exists (e.g. 2 stops, both pinned).
            return;
        }

        var target = Math.Clamp(targetOrderIndex, min, max);
        var current = currentIndex + 1;
        if (target == current)
        {
            // No-op move (own position, or clamped back onto it): short-circuit
            // before any tracking/SaveChangesAsync so nothing is written (AC-6).
            return;
        }

        var moving = stops[currentIndex];
        stops.RemoveAt(currentIndex);
        stops.Insert(target - 1, moving);

        await SetOrderAsync(collectionId, Renumber(stops), ct);
    }

    private async Task<(int? StartPoiId, int? FinishPoiId)> ReadPinsAsync(int collectionId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.PoiCollections
            .AsNoTracking()
            .Where(c => c.Id == collectionId)
            .Select(c => new { c.StartPoiId, c.FinishPoiId })
            .FirstOrDefaultAsync(ct);
        return (row?.StartPoiId, row?.FinishPoiId);
    }

    private static Dictionary<int, int> Renumber(IEnumerable<ItemRow> rows)
    {
        var desired = new Dictionary<int, int>();
        var index = 1;
        foreach (var row in rows)
        {
            desired[row.PoiId] = index++;
        }
        return desired;
    }

    /// <summary>
    /// The ONE method that writes <see cref="PoiCollectionItem.OrderIndex"/>.
    /// Applies the desired <c>PoiId → OrderIndex</c> entries to the matching
    /// tracked rows (items not in the map are left unchanged) and commits under
    /// the shared write gate. No <c>ConfigureAwait(false)</c> — Blazor Server's
    /// circuit needs the sync context.
    /// </summary>
    private async Task SetOrderAsync(int collectionId, IReadOnlyDictionary<int, int> desired, CancellationToken ct)
    {
        if (desired.Count == 0)
        {
            return;
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        var items = await db.PoiCollectionItems
            .Where(ci => ci.PoiCollectionId == collectionId)
            .ToListAsync(ct);

        var changed = false;
        foreach (var item in items)
        {
            if (desired.TryGetValue(item.PoiId, out var order) && item.OrderIndex != order)
            {
                item.OrderIndex = order;
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        await writeLock.Gate.WaitAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            writeLock.Gate.Release();
        }

        logger.LogDebug("Stop Order written for collection {CollectionId} ({Count} stop(s))",
            collectionId, desired.Count(kvp => kvp.Value > 0));
    }

    private async Task<List<ItemRow>> ReadAsync(int collectionId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.PoiCollectionItems
            .AsNoTracking()
            .Where(ci => ci.PoiCollectionId == collectionId)
            .Select(ci => new { ci.PoiId, ci.Poi.Latitude, ci.Poi.Longitude, ci.Poi.AddedDate, ci.OrderIndex })
            .ToListAsync(ct);

        // [TRIP-PLACE-01] Placeability is decided by the one canonical predicate
        // (raw coordinates are projected so the check runs in memory, not inlined
        // into the SQL expression).
        return rows
            .Select(r => new ItemRow(
                r.PoiId,
                StopPlaceability.IsPlaceable(r.Latitude, r.Longitude),
                r.AddedDate,
                r.OrderIndex))
            .ToList();
    }

    private sealed record ItemRow(int PoiId, bool Placeable, DateTime AddedDate, int Order);
}
