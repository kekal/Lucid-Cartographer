namespace LucidCartographer.Services.Trip;

/// <summary>
/// One ordered, placeable routing candidate: a stop with a Stop Order and both
/// coordinates present. [TRIP-PLACE-03] This is the shape any all-pairs routing
/// computation (the Epic 3 N×N Distance Matrix / TSP candidate set) consumes —
/// unplaceable stops never appear here. Coordinates are non-nullable by
/// construction (the accessor filters through <see cref="StopPlaceability"/>).
/// </summary>
public sealed record PlaceableStop(int PoiId, int OrderIndex, double Latitude, double Longitude);

/// <summary>
/// Owns the Stop Order (<see cref="Data.Entities.PoiCollectionItem.OrderIndex"/>)
/// for a Trip. This is the SINGLE write-path for <c>OrderIndex</c> across the
/// whole app — seed, append, compaction (and later drag/keyboard/TSP/MCP
/// reordering in Stories 1.5/3.x) all funnel through one gated method here.
/// Never mutate <c>OrderIndex</c> from a ViewModel or component.
///
/// Canonical invariant (AR-11): <c>OrderIndex</c> is 1-based, contiguous,
/// gap-free and unique across the <b>placeable</b> items of a collection
/// (a POI is placeable when both Latitude and Longitude are non-null).
/// Non-placeable items carry <c>OrderIndex == 0</c> ("not a stop").
/// </summary>
public interface ITripOrderingService
{
    /// <summary>
    /// True when the collection already has at least one persisted Stop Order
    /// (any item with <c>OrderIndex &gt; 0</c>). Used to decide whether the
    /// first toggle-on should seed a fresh order or restore the existing one.
    /// </summary>
    Task<bool> HasOrderAsync(int collectionId, CancellationToken ct = default);

    /// <summary>
    /// Returns the persisted Stop Order as <c>PoiId → OrderIndex</c> for every
    /// placeable item that has an order (<c>OrderIndex &gt; 0</c>).
    /// </summary>
    Task<IReadOnlyDictionary<int, int>> GetStopOrderAsync(int collectionId, CancellationToken ct = default);

    /// <summary>
    /// Returns the ordered <b>placeable-only</b> stop set — the routing candidate
    /// set. [TRIP-PLACE-03] Legs and any future all-pairs work (Distance Matrix,
    /// Epic 3) must consume this accessor; the full membership (including
    /// unplaceable items) stays available to the stop <i>list</i> via the
    /// ViewModel projection. Read-only: never writes <c>OrderIndex</c>.
    /// </summary>
    Task<IReadOnlyList<PlaceableStop>> GetPlaceableStopsAsync(int collectionId, CancellationToken ct = default);

    /// <summary>
    /// Seeds a deterministic Stop Order: placeable items ordered by
    /// <see cref="Data.Entities.Poi.AddedDate"/> ascending (ties broken by
    /// <c>PoiId</c> ascending) receive a contiguous 1-based index 1..N.
    /// Non-placeable items are reset to 0.
    /// </summary>
    Task SeedOrderAsync(int collectionId, CancellationToken ct = default);

    /// <summary>
    /// Appends a newly-added placeable POI as the last Stop
    /// (<c>OrderIndex = current max + 1</c>). No-op when the POI is absent or
    /// not placeable.
    /// </summary>
    Task AppendStopAsync(int collectionId, int poiId, CancellationToken ct = default);

    /// <summary>
    /// Moves a single Stop to <paramref name="targetOrderIndex"/> (1-based) and
    /// renumbers the affected range so the order stays contiguous 1..N. Covers
    /// both drag-to-position and one-step keyboard moves (Story 1.5). Pin-aware:
    /// when <see cref="Data.Entities.PoiCollection.StartPoiId"/> /
    /// <see cref="Data.Entities.PoiCollection.FinishPoiId"/> designate a Start /
    /// Finish, the target is clamped into the movable interior window and the
    /// pinned Stops never move; moving the pinned Stop itself is a no-op.
    /// Out-of-range targets clamp; a no-op move short-circuits without writing.
    /// Never changes <c>StartPoiId</c>/<c>FinishPoiId</c> (that is Story 1.7).
    /// </summary>
    Task ReorderStopAsync(int collectionId, int poiId, int targetOrderIndex, CancellationToken ct = default);

    /// <summary>
    /// Designates the Stop as the Trip's Start (Story 1.7, [TRIP-STARTFINISH-02]):
    /// writes <see cref="Data.Entities.PoiCollection.StartPoiId"/> and pins the
    /// Stop to <c>OrderIndex</c> 1 through the single ordering write path (AR-11),
    /// renumbering the remaining placeable Stops to fill 2..N in their existing
    /// relative order. Re-designation releases the prior Start (it becomes an
    /// interior Stop). No-op when the POI is not a placeable, ordered Stop of the
    /// collection or is already the Start. Throws
    /// <see cref="InvalidOperationException"/> when the POI is the current Finish
    /// (a stop cannot be both Start and Finish).
    /// </summary>
    Task SetStartAsync(int collectionId, int poiId, CancellationToken ct = default);

    /// <summary>
    /// Clears the Start designation (<c>StartPoiId = null</c>). The order stays
    /// contiguous 1..N with no pinned first Stop. No-op when no Start is set.
    /// </summary>
    Task ClearStartAsync(int collectionId, CancellationToken ct = default);

    /// <summary>
    /// Designates the Stop as the Trip's Finish ([TRIP-STARTFINISH-02]): writes
    /// <see cref="Data.Entities.PoiCollection.FinishPoiId"/> and pins the Stop to
    /// <c>OrderIndex</c> N through the single ordering write path (AR-11),
    /// renumbering interior Stops as needed. A distinct Finish makes the Trip an
    /// open path (no closing leg). Re-designation releases the prior Finish.
    /// No-op when the POI is not a placeable, ordered Stop or is already the
    /// Finish. Throws <see cref="InvalidOperationException"/> when the POI is the
    /// current Start (Finish == Start is rejected).
    /// </summary>
    Task SetFinishAsync(int collectionId, int poiId, CancellationToken ct = default);

    /// <summary>
    /// Clears the Finish designation (<c>FinishPoiId = null</c>), returning the
    /// Trip to a Roundtrip (the closing leg is restored). No-op when no Finish
    /// is set.
    /// </summary>
    Task ClearFinishAsync(int collectionId, CancellationToken ct = default);

    /// <summary>
    /// Re-compacts the order so the remaining placeable Stops are contiguous
    /// 1..N with no gap or duplicate, preserving their relative order.
    /// </summary>
    Task CompactOrderAsync(int collectionId, CancellationToken ct = default);

    /// <summary>
    /// TRIP-TSP-01 (Story 3.1, AR-6/D5): "Sort in Traveling Salesman order". Builds
    /// the on-demand N×N Distance Matrix over the placeable Stops
    /// (<see cref="IDistanceMatrixService"/>, reusing the shared cache), runs
    /// nearest-neighbour + 2-opt, and rewrites <c>OrderIndex</c> through the SAME
    /// single write path (<see cref="ITripOrderingService"/>, AR-11) as drag /
    /// keyboard / MCP — it is just another ordering write, freely overridable by a
    /// later manual drag. This is the ONLY method that sorts; the system never
    /// reorders without an explicit caller (no automatic trigger).
    ///
    /// Pin-aware: a designated Start stays at Order 1 and a designated Finish at
    /// Order N (interior edges only). A Roundtrip (no distinct Finish) closes the
    /// loop; an open path does not. The new order's total travel time is GUARANTEED
    /// <b>≤</b> the pre-sort order for the same Stops/mode — if the search cannot
    /// improve on the current order, the current order is kept (no worse result is
    /// ever written). No-op for a collection with fewer than two placeable Stops.
    /// </summary>
    Task SortTravelingSalesmanAsync(int collectionId, CancellationToken ct = default);

    /// <summary>
    /// TRIP-MCP-01 (Story 3.2, AR-8/FR-16): assigns a full Stop Order supplied by an
    /// external caller (the MCP agent). <paramref name="orderedPoiIds"/> must be
    /// EXACTLY the collection's placeable, ordered Stops — every Stop present once,
    /// no unknown / unplaceable / duplicate id — otherwise an
    /// <see cref="ArgumentException"/> is thrown (the MCP runtime surfaces it as a
    /// tool error). The supplied sequence is the interior order; a designated Start
    /// stays at Order 1 and a designated Finish at Order N (pins win, via the shared
    /// <c>ArrangeWithPins</c>). Writes through the SAME single <c>OrderIndex</c> path
    /// (1-based, contiguous, gap-free, unique — AR-11) as drag / keyboard / TSP, so an
    /// MCP-assigned order persists identically to a manual drag and stays drag-editable.
    /// </summary>
    Task AssignOrderAsync(int collectionId, IReadOnlyList<int> orderedPoiIds, CancellationToken ct = default);

    /// <summary>
    /// TRIP-DWELL-01 / TRIP-MCP-01: persists the dwell time (minutes) on the
    /// collection's membership for <paramref name="poiId"/>. <paramref name="minutes"/>
    /// is stored verbatim on <c>PoiCollectionItem.DwellMinutes</c>; <c>null</c> clears
    /// it. Written under the shared <see cref="SqliteWriteLock"/>. No-op when the
    /// membership is absent or <paramref name="minutes"/> is out of range
    /// (<c>&lt; 0</c> or <c>&gt; <see cref="TripOrderingService.MaxDwellMinutes"/></c>).
    /// The single dwell-write implementation shared by the UI (TripViewModel) and MCP.
    /// </summary>
    Task SetDwellMinutesAsync(int collectionId, int poiId, int? minutes, CancellationToken ct = default);

    /// <summary>
    /// Reconciles the order after an arbitrary membership change: any placeable
    /// item that has no order yet (<c>OrderIndex == 0</c>) is appended after the
    /// existing Stops (by AddedDate, then PoiId), then the whole set is compacted
    /// to a contiguous 1..N. Handles both additions and removals in one pass and
    /// is idempotent.
    /// </summary>
    Task ReconcileOrderAsync(int collectionId, CancellationToken ct = default);
}
