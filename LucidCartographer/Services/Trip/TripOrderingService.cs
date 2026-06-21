using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Services.Trip;

/// <summary>
/// Sole writer of <see cref="PoiCollectionItem.OrderIndex"/> and <see cref="PoiCollectionItem.OutgoingTravelMode"/>.
/// All order changes commit through <see cref="SetOrderAsync"/> under <see cref="SqliteWriteLock"/> to prevent
/// collisions with concurrent enrichment / dedup writes.
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
        // Null-coordinate check inlined here (and in GetStopOrderAsync) because EF must
        // translate it to SQL; keep in lockstep with StopPlaceability canonical predicate.
        // Only placeable items count as ordered to guard against stray OrderIndex on non-placeable rows.
        return await db.PoiCollectionItems
            .AnyAsync(ci => ci.PoiCollectionId == collectionId && ci.OrderIndex > 0
                && ci.Poi.Latitude != null && ci.Poi.Longitude != null, ct);
    }

    public async Task<IReadOnlyDictionary<int, int>> GetStopOrderAsync(int collectionId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.PoiCollectionItems
            .AsNoTracking()
            // Only placeable, ordered items are Stops; non-placeable rows with stray OrderIndex must be filtered out.
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

        // Filter through canonical predicate: unplaceable stops (null lat OR lon) never enter routing computation.
        return rows
            .Where(r => StopPlaceability.IsPlaceable(r.Latitude, r.Longitude))
            .Select(r => new PlaceableStop(r.PoiId, r.OrderIndex, r.Latitude!.Value, r.Longitude!.Value))
            .ToList();
    }

    public async Task SeedOrderAsync(int collectionId, CancellationToken ct = default)
    {
        var rows = await ReadAsync(collectionId, ct);

        // Seed 1-based contiguous order by AddedDate asc, tie-broken by PoiId asc; non-placeable stay 0.
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
        // Build matrix over placeable Stops (using shared cache), run NN + 2-opt, commit through
        // the same Renumber + SetOrderAsync as seed/reorder/designation (sole writer pattern).
        // Fewer than two placeable Stops ⇒ nothing to sort.
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

        // Keep optimized tour only if strictly better; otherwise retain current order (never-worse guard).
        var chosen = solvedCost < preCost - 1e-6 ? solved : identity;

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

        // Externally-supplied full order must be EXACTLY the current placeable Stop set;
        // validate before touching order to fail loudly on agent mistakes.
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
        // Out-of-range is silent no-op (caller pre-validates); shared by UI and MCP.
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

    public async Task SetOutgoingTravelModeAsync(int collectionId, int fromPoiId, string? mode, CancellationToken ct = default)
    {
        // Sole writer of a single leg's mode; null (≡ AnyAir) allowed, otherwise must be valid TravelMode.
        if (mode is not null && !TravelMode.IsValid(mode))
        {
            throw new ArgumentException(
                $"'{mode}' is not a valid travel mode; expected null or one of {string.Join(", ", TravelMode.All)}.",
                nameof(mode));
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        var membership = await db.PoiCollectionItems.FirstOrDefaultAsync(
            ci => ci.PoiCollectionId == collectionId && ci.PoiId == fromPoiId, ct);
        if (membership is null || membership.OutgoingTravelMode == mode)
        {
            // Absent membership or unchanged mode — no write, no order change.
            return;
        }

        membership.OutgoingTravelMode = mode;

        // Single gated write (not routed through SetOrderAsync — setting mode never reorders).
        await writeLock.Gate.WaitAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            writeLock.Gate.Release();
        }

        logger.LogDebug("TRIP-LEGMODE-01: outgoing travel mode {Mode} written for POI {PoiId} in collection {CollectionId}",
            mode ?? "(null/AnyAir)", fromPoiId, collectionId);
    }

    public async Task SetAllOutgoingTravelModesAsync(
        int collectionId, string? mode, bool overwriteExisting, CancellationToken ct = default)
    {
        // Bulk assignment in single gated transaction; validate up front like SetOutgoingTravelModeAsync.
        if (mode is not null && !TravelMode.IsValid(mode))
        {
            throw new ArgumentException(
                $"'{mode}' is not a valid travel mode; expected null or one of {string.Join(", ", TravelMode.All)}.",
                nameof(mode));
        }

        await using var db = await factory.CreateDbContextAsync(ct);

        // The collection's Finish pin decides whether the last ordered stop is a leg
        // From-stop: a distinct Finish makes an open path (the Finish departs no leg),
        // while a Roundtrip closes back to the Start (the last stop departs the closing
        // leg). Mirrors TripViewModel.BuildLegs / DirectionalPairs.
        var collection = await db.PoiCollections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == collectionId, ct);
        if (collection is null)
        {
            return;
        }

        // Ordered placeable stops (Start pinned to OrderIndex 1, Finish to N), tracked
        // for write. Same placeability predicate as the rest of the service.
        var stops = await db.PoiCollectionItems
            .Where(ci => ci.PoiCollectionId == collectionId && ci.OrderIndex > 0
                && ci.Poi.Latitude != null && ci.Poi.Longitude != null)
            .OrderBy(ci => ci.OrderIndex)
            .ToListAsync(ct);

        if (stops.Count < 2)
        {
            // Fewer than two placeable stops ⇒ no legs ⇒ nothing to assign.
            return;
        }

        var finishIsDistinctStop = collection.FinishPoiId is { } fid
            && fid != stops[0].PoiId
            && stops.Any(s => s.PoiId == fid);

        // From-stops: every leg's origin. On an open path the Finish (last) stop departs
        // no leg and is excluded; on a Roundtrip the last stop departs the closing leg.
        var fromStopCount = finishIsDistinctStop ? stops.Count - 1 : stops.Count;

        var changed = false;
        for (var i = 0; i < fromStopCount; i++)
        {
            var membership = stops[i];
            // Overwrite-off: only fill the undefined Any/Air legs (null or the explicit
            // AnyAir value). A leg with an explicit ground mode is left untouched.
            var isUnset = membership.OutgoingTravelMode is null
                || membership.OutgoingTravelMode == TravelMode.AnyAir;
            if (!overwriteExisting && !isUnset)
            {
                continue;
            }
            if (membership.OutgoingTravelMode == mode)
            {
                continue;
            }
            membership.OutgoingTravelMode = mode;
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        // Single gated write (not routed through SetOrderAsync — setting mode never reorders).
        await writeLock.Gate.WaitAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            writeLock.Gate.Release();
        }

        logger.LogDebug(
            "TRIP-BULKMODE-01: bulk outgoing travel mode {Mode} (overwrite={Overwrite}) written across {Count} From-stops in collection {CollectionId}",
            mode ?? "(null/AnyAir)", overwriteExisting, fromStopCount, collectionId);
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

        // Existing Stops first (by current order), then unordered placeable items in AddedDate/PoiId order.
        // Renumber whole sequence 1..N; non-placeable stay 0.
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
        // Release orphaned pins: a pin whose POI is no longer a placeable Stop prevents
        // phantom open path (FinishPoiId is null but BuildLegs draws closing leg from non-Stop).
        var reconciledStart = startPoiId is { } sid && live.Contains(sid) ? startPoiId : null;
        var reconciledFinish = finishPoiId is { } fid && live.Contains(fid) ? finishPoiId : null;
        if (reconciledStart != startPoiId || reconciledFinish != finishPoiId)
        {
            await WritePinsAsync(collectionId, reconciledStart, reconciledFinish, ct);
        }

        var desired = Renumber(ArrangeWithPins(sequence, reconciledStart, reconciledFinish));
        // Reset stale OrderIndex on POIs that just became Unplaceable to keep stored order in sync with placeability.
        foreach (var row in rows.Where(r => !r.Placeable))
        {
            desired[row.PoiId] = 0;
        }

        // Pin reconcile may flip trip shape (removed Finish ⇒ roundtrip); pass prior Finish so
        // SetOrderAsync resets the affected leg's mode.
        await SetOrderAsync(collectionId, desired, ct, previousShape: (true, finishPoiId));
    }

    public async Task ReorderStopAsync(int collectionId, int poiId, int targetOrderIndex, CancellationToken ct = default)
    {
        var rows = await ReadAsync(collectionId, ct);

        // Drag and keyboard both use shared Renumber + SetOrderAsync; single remove + insert + renumber
        // ensures contiguous, gap-free, unique result by construction (sole writer pattern).
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
            // No-op move (clamped back to own position): short-circuit before writing.
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

    // All four designation paths land here: pins written first, then Stop sequence rebuilt
    // (pinned Start first, interior Stops in order, pinned Finish last) and renumbered through
    // shared Renumber + SetOrderAsync, ensuring 1..N contiguous unique result (sole writer pattern).
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
            // Absent, unplaceable, or unordered — not a Start/Finish candidate.
            return;
        }

        var newStart = pinIsStart ? poiId : startPoiId;
        var newFinish = pinIsStart ? finishPoiId : poiId;

        // Re-designation releases old pin implicitly: prior endpoint renumbers into interior slot.
        await WritePinsAsync(collectionId, newStart, newFinish, ct);
        await RenumberWithPinsAsync(collectionId, rows, newStart, newFinish, ct, previousShape: (true, finishPoiId));
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

        // Clearing a pin never reshuffles — endpoint stays in place. Re-validate through
        // same path anyway (idempotent: SetOrderAsync writes nothing if desired == stored).
        var rows = await ReadAsync(collectionId, ct);
        await RenumberWithPinsAsync(collectionId, rows, newStart, newFinish, ct, previousShape: (true, finishPoiId));
    }

    /// <summary>
    /// Rebuilds placeable Stop sequence with pinned endpoints in slots (Start→1, Finish→N, interior in order)
    /// and commits via the sole OrderIndex writer. Pins only count when POI is actually a Stop.
    /// </summary>
    private async Task RenumberWithPinsAsync(
        int collectionId, List<ItemRow> rows, int? startPoiId, int? finishPoiId, CancellationToken ct,
        (bool Provided, int? Finish) previousShape = default)
    {
        var stops = rows.Where(r => r.Placeable && r.Order > 0)
            .OrderBy(r => r.Order)
            .ThenBy(r => r.AddedDate)
            .ThenBy(r => r.PoiId)
            .ToList();

        await SetOrderAsync(
            collectionId, Renumber(ArrangeWithPins(stops, startPoiId, finishPoiId)), ct, previousShape);
    }

    /// <summary>
    /// Arranges Stop sequence with pinned endpoints in slots (Start first, Finish last, others in order).
    /// Pins not in sequence are ignored. Single implementation of "Start→1 / Finish→N" rule shared by reconcile and designation.
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
    /// Writes StartPoiId/FinishPoiId on tracked PoiCollection under shared write gate.
    /// Version concurrency token prevents silent lost updates from concurrent editors.
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
    /// Sole writer of <see cref="PoiCollectionItem.OrderIndex"/> and <see cref="PoiCollectionItem.OutgoingTravelMode"/>.
    /// Nulls mode for Stops whose successor changed; unchanged legs keep their mode. Both changes commit atomically under write gate.
    /// </summary>
    // <paramref name="previousShape"/> (provided only by pin-flip/reconcile) allows computing the OLD successor map
    // under prior trip shape, enabling roundtrip↔open-path flips to correctly reset the appearing/vanishing closing leg's mode.
    private async Task SetOrderAsync(
        int collectionId, IReadOnlyDictionary<int, int> desired, CancellationToken ct,
        (bool Provided, int? Finish) previousShape = default)
    {
        if (desired.Count == 0)
        {
            return;
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        var items = await db.PoiCollectionItems
            .Where(ci => ci.PoiCollectionId == collectionId)
            .Include(ci => ci.Poi)
            .ToListAsync(ct);

        // The trip shape (roundtrip vs open path) decides the LAST stop's successor;
        // read the pins exactly as BuildLegs does. A distinct Finish that is a real
        // placeable Stop other than the first ⇒ open path (no closing leg); anything
        // else ⇒ roundtrip (closing leg last→first).
        var (startPoiId, finishPoiId) = await ReadPinsAsync(collectionId, ct);

        // Map each item to its OLD and NEW OrderIndex. Items absent from `desired`
        // keep their current OrderIndex (e.g. AppendStopAsync passes one entry).
        bool IsPlaceable(PoiCollectionItem ci) =>
            StopPlaceability.IsPlaceable(ci.Poi.Latitude, ci.Poi.Longitude);

        int NewOrderOf(PoiCollectionItem ci) =>
            desired.TryGetValue(ci.PoiId, out var o) ? o : ci.OrderIndex;

        // Successor PoiId for each Stop: next one in ordered sequence, or first (roundtrip) / none (open path) for last.
        Dictionary<int, int?> SuccessorMap(Func<PoiCollectionItem, int> orderOf, int? finishForShape)
        {
            var sequence = items
                .Where(ci => IsPlaceable(ci) && orderOf(ci) > 0)
                .OrderBy(orderOf)
                .ThenBy(ci => ci.PoiId)
                .ToList();

            var map = new Dictionary<int, int?>(sequence.Count);
            for (var k = 0; k < sequence.Count; k++)
            {
                map[sequence[k].PoiId] = k + 1 < sequence.Count ? sequence[k + 1].PoiId : null;
            }

            if (sequence.Count > 0)
            {
                var first = sequence[0];
                var last = sequence[^1];
                // Open path when distinct Finish resolves to real Stop other than first; otherwise roundtrip.
                var finishIsDistinctStop = finishForShape is { } fid
                    && fid != first.PoiId
                    && sequence.Any(s => s.PoiId == fid);
                map[last.PoiId] = finishIsDistinctStop ? null : first.PoiId;
            }

            return map;
        }

        // The OLD map uses the prior trip shape (previousFinishPoiId when supplied by a
        // pin-flip path; else the current Finish ⇒ no shape change); the NEW map always
        // uses the current Finish. This makes a roundtrip↔open-path flip reset the
        // closing leg's mode even when the OrderIndex itself didn't change (H1).
        var oldFinishForShape = previousShape.Provided ? previousShape.Finish : finishPoiId;
        var oldSucc = SuccessorMap(ci => ci.OrderIndex, oldFinishForShape);
        var newSucc = SuccessorMap(NewOrderOf, finishPoiId);

        // Snapshot OLD OrderIndex to distinguish stops with existing legs (oldOrder > 0) from
        // those entering the sequence for the first time (oldOrder 0). Only existing legs may have stale mode to reset.
        var oldOrders = items.ToDictionary(ci => ci.PoiId, ci => ci.OrderIndex);

        var changed = false;
        foreach (var item in items)
        {
            var newOrder = NewOrderOf(item);
            if (item.OrderIndex != newOrder)
            {
                item.OrderIndex = newOrder;
                changed = true;
            }

            // Reset mode for placeable Stops with existing legs (oldOrder > 0) whose successor changed.
            // Unplaceable items and stops entering sequence for first time are skipped.
            if (IsPlaceable(item) && newOrder > 0 && oldOrders.GetValueOrDefault(item.PoiId) > 0)
            {
                var oldS = oldSucc.GetValueOrDefault(item.PoiId);
                var newS = newSucc.GetValueOrDefault(item.PoiId);
                if (oldS != newS && item.OutgoingTravelMode is not null)
                {
                    item.OutgoingTravelMode = null;
                    changed = true;
                }
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

        // Coordinates projected so canonical placeability check runs in memory, not inlined into SQL.
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
