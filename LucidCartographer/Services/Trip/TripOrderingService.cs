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
    IDistanceMatrixService distanceMatrix,
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

    public async Task SortTravelingSalesmanAsync(int collectionId, CancellationToken ct = default)
    {
        // TRIP-TSP-01 (AR-6/D5): build the on-demand matrix over the placeable Stops
        // (reusing the shared cache), run NN + 2-opt, then commit through the SAME
        // Renumber + SetOrderAsync the seed/reorder/designation paths use (AR-11 single
        // writer). Fewer than two placeable Stops ⇒ nothing to sort.
        var matrix = await distanceMatrix.BuildAsync(collectionId, ct);
        if (matrix is null || matrix.Stops.Count < 2)
        {
            return;
        }

        var n = matrix.Stops.Count;
        var (startPoiId, finishPoiId) = await ReadPinsAsync(collectionId, ct);

        // Pin → matrix index (null when the pinned POI is not actually a Stop here —
        // defensive, mirrors ReorderStopAsync/ArrangeWithPins).
        var startIndex = MatrixIndexOf(matrix.Stops, startPoiId);
        var finishIndex = MatrixIndexOf(matrix.Stops, finishPoiId);
        if (startIndex is { } si && finishIndex == si)
        {
            // Start == Finish can't happen (SetPin rejects it); treat as no Finish pin.
            finishIndex = null;
        }

        // A distinct Finish Stop makes the trip an open path (no closing leg);
        // otherwise it is a Roundtrip whose cost includes the closing edge — the
        // same open/closed shape TravelTimeComputationBackgroundService.DirectionalPairs
        // and TripViewModel.BuildLegs draw, so "≤ pre-sort total" is measured against
        // what the UI actually shows.
        var roundtrip = finishIndex is null;

        // Pre-sort tour = the current Stop Order (matrix.Stops is ordered by
        // OrderIndex, so the identity permutation IS the current order).
        var identity = Enumerable.Range(0, n).ToList();
        var preCost = TspSolver.TourCost(identity, matrix.DurationSeconds, roundtrip);

        var solved = TspSolver.Solve(matrix.DurationSeconds, n, startIndex, finishIndex, roundtrip);
        var solvedCost = TspSolver.TourCost(solved, matrix.DurationSeconds, roundtrip);

        // AC4 never-worse guard: keep the optimized tour only when it is strictly
        // better; otherwise retain the current order. The result is therefore always
        // ≤ the pre-sort total — never worse — regardless of matrix shape.
        var chosen = solvedCost < preCost - 1e-6 ? solved : identity;

        // Map the chosen index permutation back to the tracked ItemRows, then run it
        // through the shared pin arrangement (Start→1 / Finish→N) and the one writer.
        var rows = await ReadAsync(collectionId, ct);
        var rowByPoiId = rows.ToDictionary(r => r.PoiId);
        var orderedRows = chosen
            .Select(idx => rowByPoiId[matrix.Stops[idx].PoiId])
            .ToList();

        var desired = Renumber(ArrangeWithPins(orderedRows, startPoiId, finishPoiId));
        await SetOrderAsync(collectionId, desired, ct);

        logger.LogDebug(
            "TRIP-TSP-01: sorted collection {CollectionId} over {Count} stop(s); pre {PreCost}s, post {PostCost}s, {Outcome}",
            collectionId, n, (long)preCost, (long)solvedCost,
            chosen == solved ? "improved" : "kept-existing");
    }

    /// <summary>
    /// Upper bound on dwell minutes: 60 days. Mirrors the former
    /// <c>TripViewModel.MaxDwellMinutes</c> (now centralized here so the UI and MCP
    /// share one bound).
    /// </summary>
    public const int MaxDwellMinutes = 60 * 24 * 60;

    public async Task AssignOrderAsync(int collectionId, IReadOnlyList<int> orderedPoiIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(orderedPoiIds);

        // TRIP-MCP-01 (AR-11 single writer): an externally-supplied full order. The
        // valid input is EXACTLY the current placeable Stop set — validate before
        // touching the order so an agent mistake fails loudly rather than silently
        // dropping or duplicating a Stop.
        var rows = await ReadAsync(collectionId, ct);
        var stops = rows.Where(r => r.Placeable && r.Order > 0).ToList();
        var stopIds = stops.Select(r => r.PoiId).ToHashSet();

        if (orderedPoiIds.Count != stopIds.Count
            || orderedPoiIds.Distinct().Count() != orderedPoiIds.Count
            || !orderedPoiIds.All(stopIds.Contains))
        {
            throw new ArgumentException(
                $"orderedPoiIds must be exactly the {stopIds.Count} placeable Stop(s) of collection {collectionId}, " +
                "each listed once (no unknown, unplaceable, missing or duplicate id).",
                nameof(orderedPoiIds));
        }

        var rowByPoiId = stops.ToDictionary(r => r.PoiId);
        var orderedRows = orderedPoiIds.Select(id => rowByPoiId[id]).ToList();

        // Pins win: a designated Start/Finish keeps Order 1 / N regardless of the
        // supplied position — same arrangement the drag/TSP/designation paths use.
        var (startPoiId, finishPoiId) = await ReadPinsAsync(collectionId, ct);
        var desired = Renumber(ArrangeWithPins(orderedRows, startPoiId, finishPoiId));
        await SetOrderAsync(collectionId, desired, ct);

        logger.LogDebug("TRIP-MCP-01: assigned external Stop Order for collection {CollectionId} ({Count} stop(s))",
            collectionId, orderedRows.Count);
    }

    public async Task SetDwellMinutesAsync(int collectionId, int poiId, int? minutes, CancellationToken ct = default)
    {
        // TRIP-DWELL-01: validated dwell persist shared by the UI and MCP. Out-of-range
        // is a silent no-op (the caller pre-validates / surfaces UX), mirroring the
        // former VM behavior.
        if (minutes is < 0 or > MaxDwellMinutes)
        {
            return;
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        var membership = await db.PoiCollectionItems.FirstOrDefaultAsync(
            ci => ci.PoiCollectionId == collectionId && ci.PoiId == poiId, ct);
        if (membership is null || membership.DwellMinutes == minutes)
        {
            return;
        }

        membership.DwellMinutes = minutes;

        await writeLock.Gate.WaitAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            writeLock.Gate.Release();
        }

        logger.LogDebug("TRIP-DWELL-01: dwell {Minutes} written for POI {PoiId} in collection {CollectionId}",
            minutes, poiId, collectionId);
    }

    private static int? MatrixIndexOf(IReadOnlyList<PlaceableStop> stops, int? poiId)
    {
        if (poiId is not { } id)
        {
            return null;
        }
        for (var i = 0; i < stops.Count; i++)
        {
            if (stops[i].PoiId == id)
            {
                return i;
            }
        }
        return null;
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
        var sequence = ordered.Concat(appended).ToList();

        // TRIP-STARTFINISH-07 ([Review][Patch]): reconcile the Start/Finish pins
        // against the live placeable membership. A pin whose POI is no longer a
        // placeable Stop — coordinates cleared (now Unplaceable, OrderIndex 0) or
        // removed from this collection — is RELEASED. An orphaned pin would leave
        // IsRoundtrip (FinishPoiId is null) disagreeing with the drawn closing leg
        // (BuildLegs falls back to a closing leg when the Finish is not a real
        // stop), producing a "phantom open path" with no visible Finish row to
        // clear it (Story 1.7 AC6 "no orphaned pins"). Surviving pins are then
        // arranged into their slots (Start → 1, Finish → N) so a newly appended
        // Stop never demotes a pinned Finish out of the last slot.
        var (startPoiId, finishPoiId) = await ReadPinsAsync(collectionId, ct);
        var live = sequence.Select(r => r.PoiId).ToHashSet();
        var reconciledStart = startPoiId is { } sid && live.Contains(sid) ? startPoiId : null;
        var reconciledFinish = finishPoiId is { } fid && live.Contains(fid) ? finishPoiId : null;
        if (reconciledStart != startPoiId || reconciledFinish != finishPoiId)
        {
            await WritePinsAsync(collectionId, reconciledStart, reconciledFinish, ct);
        }

        var desired = Renumber(ArrangeWithPins(sequence, reconciledStart, reconciledFinish));
        // A POI that just became Unplaceable may still carry a stale OrderIndex
        // from when it was a Stop — reset it to 0 ("not a stop"), mirroring
        // SeedOrderAsync, so the stored order never disagrees with placeability.
        foreach (var row in rows.Where(r => !r.Placeable))
        {
            desired[row.PoiId] = 0;
        }

        await SetOrderAsync(collectionId, desired, ct);
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

    // === Start/Finish designation (Story 1.7) ===

    public Task SetStartAsync(int collectionId, int poiId, CancellationToken ct = default) =>
        SetPinAsync(collectionId, poiId, pinIsStart: true, ct);

    public Task ClearStartAsync(int collectionId, CancellationToken ct = default) =>
        ClearPinAsync(collectionId, pinIsStart: true, ct);

    public Task SetFinishAsync(int collectionId, int poiId, CancellationToken ct = default) =>
        SetPinAsync(collectionId, poiId, pinIsStart: false, ct);

    public Task ClearFinishAsync(int collectionId, CancellationToken ct = default) =>
        ClearPinAsync(collectionId, pinIsStart: false, ct);

    // TRIP-STARTFINISH-02 (AR-11 single writer): all four designation paths land
    // here. The pin fields (StartPoiId/FinishPoiId) are written first, then the
    // placeable Stop sequence is rebuilt — pinned Start first, interior Stops in
    // their existing relative order, pinned Finish last — and renumbered through
    // the SAME Renumber + SetOrderAsync the seed/compaction/reorder paths use, so
    // the result is contiguous, gap-free and unique 1..N by construction and no
    // stop can ever hold two Stop Order values.
    private async Task SetPinAsync(int collectionId, int poiId, bool pinIsStart, CancellationToken ct)
    {
        var (startPoiId, finishPoiId) = await ReadPinsAsync(collectionId, ct);

        // A stop cannot be both Start and Finish — reject the cross-designation
        // (the UI surfaces this as a disabled control; the service stays the
        // authoritative guard for any other caller, e.g. MCP).
        if (pinIsStart && poiId == finishPoiId)
        {
            throw new InvalidOperationException(
                $"POI {poiId} is the current Finish of collection {collectionId}; a stop cannot be both Start and Finish.");
        }
        if (!pinIsStart && poiId == startPoiId)
        {
            throw new InvalidOperationException(
                $"POI {poiId} is the current Start of collection {collectionId}; a stop cannot be both Start and Finish.");
        }

        if ((pinIsStart ? startPoiId : finishPoiId) == poiId)
        {
            // Already designated — idempotent no-op, nothing to write.
            return;
        }

        var rows = await ReadAsync(collectionId, ct);
        var target = rows.FirstOrDefault(r => r.PoiId == poiId);
        if (target is null || !target.Placeable || target.Order <= 0)
        {
            // Absent, unplaceable (OrderIndex 0, excluded from routing) or
            // unordered — not a Start/Finish candidate ([TRIP-PLACE-01] guard).
            return;
        }

        var newStart = pinIsStart ? poiId : startPoiId;
        var newFinish = pinIsStart ? finishPoiId : poiId;

        // Re-designation releases the old pin implicitly: the prior endpoint is
        // simply no longer first/last in the rebuilt sequence and renumbers into
        // an interior slot — no gap, no duplicate.
        await WritePinsAsync(collectionId, newStart, newFinish, ct);
        await RenumberWithPinsAsync(collectionId, rows, newStart, newFinish, ct);
    }

    private async Task ClearPinAsync(int collectionId, bool pinIsStart, CancellationToken ct)
    {
        var (startPoiId, finishPoiId) = await ReadPinsAsync(collectionId, ct);
        if ((pinIsStart ? startPoiId : finishPoiId) is null)
        {
            return;
        }

        var newStart = pinIsStart ? null : startPoiId;
        var newFinish = pinIsStart ? finishPoiId : null;
        await WritePinsAsync(collectionId, newStart, newFinish, ct);

        // Clearing a pin never reshuffles the order — the former endpoint stays
        // where it is, the sequence is already contiguous 1..N. Re-validate
        // through the same path anyway (idempotent: SetOrderAsync writes nothing
        // when the desired order equals the stored one).
        var rows = await ReadAsync(collectionId, ct);
        await RenumberWithPinsAsync(collectionId, rows, newStart, newFinish, ct);
    }

    /// <summary>
    /// Rebuilds the placeable Stop sequence with the pinned endpoints in their
    /// slots (Start → 1, Finish → N, interior compacted to fill the middle in
    /// existing relative order) and commits via the one OrderIndex writer.
    /// Pins only count when the designated POI actually is a Stop (defensive —
    /// mirrors ReorderStopAsync).
    /// </summary>
    private async Task RenumberWithPinsAsync(
        int collectionId, List<ItemRow> rows, int? startPoiId, int? finishPoiId, CancellationToken ct)
    {
        var stops = rows.Where(r => r.Placeable && r.Order > 0)
            .OrderBy(r => r.Order)
            .ThenBy(r => r.AddedDate)
            .ThenBy(r => r.PoiId)
            .ToList();

        await SetOrderAsync(collectionId, Renumber(ArrangeWithPins(stops, startPoiId, finishPoiId)), ct);
    }

    /// <summary>
    /// Arranges an already-ordered Stop sequence with the pinned endpoints in
    /// their slots: pinned Start first, pinned Finish last, every other Stop in
    /// its given relative order. A pin whose POI is not in the sequence is simply
    /// ignored (the caller is responsible for releasing orphaned pins). Shared by
    /// the reconcile and designation paths so the "Start→1 / Finish→N" rule has a
    /// single implementation.
    /// </summary>
    private static List<ItemRow> ArrangeWithPins(IReadOnlyList<ItemRow> stopsInOrder, int? startPoiId, int? finishPoiId)
    {
        var start = startPoiId is { } sid ? stopsInOrder.FirstOrDefault(s => s.PoiId == sid) : null;
        var finish = finishPoiId is { } fid ? stopsInOrder.FirstOrDefault(s => s.PoiId == fid) : null;

        var sequence = new List<ItemRow>(stopsInOrder.Count);
        if (start is not null)
        {
            sequence.Add(start);
        }
        sequence.AddRange(stopsInOrder.Where(s => s != start && s != finish));
        if (finish is not null)
        {
            sequence.Add(finish);
        }

        return sequence;
    }

    /// <summary>
    /// Writes StartPoiId/FinishPoiId on the tracked PoiCollection under the
    /// shared write gate. The Version concurrency token is bumped centrally by
    /// AppDbContext.SaveChanges for every modified PoiCollection, so a concurrent
    /// editor of the same collection surfaces as a DbUpdateConcurrencyException
    /// rather than a silent lost update.
    /// </summary>
    private async Task WritePinsAsync(int collectionId, int? startPoiId, int? finishPoiId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var collection = await db.PoiCollections.FirstOrDefaultAsync(c => c.Id == collectionId, ct);
        if (collection is null || (collection.StartPoiId == startPoiId && collection.FinishPoiId == finishPoiId))
        {
            return;
        }

        collection.StartPoiId = startPoiId;
        collection.FinishPoiId = finishPoiId;

        await writeLock.Gate.WaitAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            writeLock.Gate.Release();
        }

        logger.LogDebug("Start/Finish pins written for collection {CollectionId} (Start {StartPoiId}, Finish {FinishPoiId})",
            collectionId, startPoiId, finishPoiId);
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
