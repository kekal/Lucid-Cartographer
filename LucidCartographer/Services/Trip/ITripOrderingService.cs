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
    /// Re-compacts the order so the remaining placeable Stops are contiguous
    /// 1..N with no gap or duplicate, preserving their relative order.
    /// </summary>
    Task CompactOrderAsync(int collectionId, CancellationToken ct = default);

    /// <summary>
    /// Reconciles the order after an arbitrary membership change: any placeable
    /// item that has no order yet (<c>OrderIndex == 0</c>) is appended after the
    /// existing Stops (by AddedDate, then PoiId), then the whole set is compacted
    /// to a contiguous 1..N. Handles both additions and removals in one pass and
    /// is idempotent.
    /// </summary>
    Task ReconcileOrderAsync(int collectionId, CancellationToken ct = default);
}
