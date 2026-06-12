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
        return await db.PoiCollectionItems
            .AsNoTracking()
            .Where(ci => ci.PoiCollectionId == collectionId)
            .Select(ci => new ItemRow(
                ci.PoiId,
                ci.Poi.Latitude != null && ci.Poi.Longitude != null,
                ci.Poi.AddedDate,
                ci.OrderIndex))
            .ToListAsync(ct);
    }

    private sealed record ItemRow(int PoiId, bool Placeable, DateTime AddedDate, int Order);
}
