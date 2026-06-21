namespace LucidCartographer.Services.Trip;

/// <summary>
/// One ordered, placeable routing candidate: a stop with a Stop Order and both
/// coordinates present. This is the shape any all-pairs routing computation
/// (N×N Distance Matrix / TSP candidate set) consumes — unplaceable stops never
/// appear here. Coordinates are non-nullable by construction.
/// </summary>
public sealed record PlaceableStop(int PoiId, int OrderIndex, double Latitude, double Longitude);

/// <summary>
/// Owns the Stop Order (<see cref="Data.Entities.PoiCollectionItem.OrderIndex"/>)
/// for a Trip. This is the SINGLE write-path for <c>OrderIndex</c> across the
/// whole app — seed, append, compaction, and reordering all funnel through
/// gated methods here. Never mutate <c>OrderIndex</c> from a ViewModel or component.
///
/// Invariant: <c>OrderIndex</c> is 1-based, contiguous, gap-free, and unique
/// across placeable items (both Latitude and Longitude non-null).
/// Non-placeable items carry <c>OrderIndex == 0</c>.
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
    /// Returns the ordered placeable-only stop set — the routing candidate set.
    /// Legs and all-pairs work must consume this accessor; full membership
    /// stays available via the ViewModel projection. Read-only: never writes <c>OrderIndex</c>.
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
    /// renumbers the affected range so the order stays contiguous 1..N. Pin-aware:
    /// designated Start/Finish stops are clamped to 1/N and never reorder.
    /// Out-of-range targets clamp; no-op moves short-circuit without writing.
    /// </summary>
    Task ReorderStopAsync(int collectionId, int poiId, int targetOrderIndex, CancellationToken ct = default);

    /// <summary>
    /// Designates the Stop as the Trip's Start: writes <see cref="Data.Entities.PoiCollection.StartPoiId"/>
    /// and pins to <c>OrderIndex</c> 1, renumbering remaining stops to 2..N in existing order.
    /// Re-designation releases the prior Start. Throws <see cref="InvalidOperationException"/>
    /// when the POI is the current Finish.
    /// </summary>
    Task SetStartAsync(int collectionId, int poiId, CancellationToken ct = default);

    /// <summary>
    /// Clears the Start designation (<c>StartPoiId = null</c>). The order stays
    /// contiguous 1..N with no pinned first Stop. No-op when no Start is set.
    /// </summary>
    Task ClearStartAsync(int collectionId, CancellationToken ct = default);

    /// <summary>
    /// Designates the Stop as the Trip's Finish: writes <see cref="Data.Entities.PoiCollection.FinishPoiId"/>
    /// and pins to <c>OrderIndex</c> N, renumbering interior stops as needed.
    /// A distinct Finish makes the Trip an open path (no closing leg).
    /// Throws <see cref="InvalidOperationException"/> when the POI is the current Start.
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
    /// Sorts stops in Traveling Salesman order: builds N×N Distance Matrix over
    /// placeable stops, runs nearest-neighbour + 2-opt, and rewrites through the
    /// single ordering write path — freely overridable by manual drag. Pin-aware:
    /// designated Start/Finish stay at 1/N. New travel time is guaranteed ≤ pre-sort
    /// (current order kept if no improvement). No-op for fewer than two stops.
    /// </summary>
    Task SortTravelingSalesmanAsync(int collectionId, CancellationToken ct = default);

    /// <summary>
    /// Assigns a full Stop Order from an external caller (e.g. MCP agent).
    /// <paramref name="orderedPoiIds"/> must be exactly the collection's placeable,
    /// ordered stops — every stop present once. Designated Start/Finish stay at 1/N.
    /// MCP-assigned orders persist identically to manual drag and stay drag-editable.
    /// </summary>
    Task AssignOrderAsync(int collectionId, IReadOnlyList<int> orderedPoiIds, CancellationToken ct = default);

    /// <summary>
    /// Persists the dwell time (minutes) on the collection membership for <paramref name="poiId"/>.
    /// <paramref name="minutes"/> is stored verbatim on <c>PoiCollectionItem.DwellMinutes</c>;
    /// <c>null</c> clears it. Written under <see cref="SqliteWriteLock"/>.
    /// </summary>
    Task SetDwellMinutesAsync(int collectionId, int poiId, int? minutes, CancellationToken ct = default);

    /// <summary>
    /// Sets ONE leg's travel mode by writing <see cref="Data.Entities.PoiCollectionItem.OutgoingTravelMode"/>
    /// on the From-stop's membership. <paramref name="mode"/> must be <c>null</c> (≡ AnyAir/undefined)
    /// or one of <see cref="Data.Entities.TravelMode.All"/>. Does NOT change Stop Order.
    /// This is the sole writer of outgoing travel mode (barring order-reset operations).
    /// </summary>
    Task SetOutgoingTravelModeAsync(int collectionId, int fromPoiId, string? mode, CancellationToken ct = default);

    /// <summary>
    /// Bulk assignment of travel mode: assigns ONE <paramref name="mode"/> to every leg's
    /// From-stop in a single transaction. Covers every ordered placeable stop except a
    /// distinct Finish (which departs no leg). When <paramref name="overwriteExisting"/> is false,
    /// only currently-unset (null/AnyAir) legs change. <paramref name="mode"/> must be <c>null</c>
    /// (≡ AnyAir) or one of <see cref="Data.Entities.TravelMode.All"/>.
    /// </summary>
    Task SetAllOutgoingTravelModesAsync(int collectionId, string? mode, bool overwriteExisting, CancellationToken ct = default);

    /// <summary>
    /// Reconciles the order after an arbitrary membership change: any placeable
    /// item that has no order yet (<c>OrderIndex == 0</c>) is appended after the
    /// existing Stops (by AddedDate, then PoiId), then the whole set is compacted
    /// to a contiguous 1..N. Handles both additions and removals in one pass and
    /// is idempotent.
    /// </summary>
    Task ReconcileOrderAsync(int collectionId, CancellationToken ct = default);
}
