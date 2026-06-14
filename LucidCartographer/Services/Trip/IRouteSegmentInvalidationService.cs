namespace LucidCartographer.Services.Trip;

/// <summary>
/// TRIP-INVALIDATE-01 (Story 2.4): centralized invalidation of cached
/// <see cref="Data.Entities.RouteSegment"/> rows. A cached leg becomes stale when
/// the geometry it was derived from changes (a POI's coordinates move) or when the
/// user explicitly asks to recompute. Invalidation simply DELETES the stale rows so
/// the existing background compute path (TRIP-TRAVELTIME-01) refills them on the
/// next trigger — there is no in-place mutation here.
///
/// Provider dimension: the shipped cache key is
/// <c>(FromPoiId, ToPoiId, TravelMode)</c> with the provider in the <c>Source</c>
/// column (AR-1). Epic 2 has exactly one active provider, so a provider change is a
/// deployment event the operator resolves via the explicit Recompute action — there
/// is no runtime provider/assumed-speed config-watcher and Provider is never part of
/// the key.
///
/// Two fidelity rules are load-bearing:
/// <list type="bullet">
/// <item>A <see cref="Data.Entities.Fidelity.Manual"/> row is user-entered, not
/// derived from coordinates — it is NEVER deleted by invalidation.</item>
/// <item>A <see cref="Data.Entities.Fidelity.Measured"/> row is a higher-fidelity
/// result that must never be silently downgraded — recompute-eligible invalidation
/// keeps it too (only <c>Estimated</c>/<c>Placeholder</c>/<c>EstimatedFallback</c>
/// rows are eligible).</item>
/// </list>
/// </summary>
public interface IRouteSegmentInvalidationService
{
    /// <summary>
    /// Deletes every cached <see cref="Data.Entities.RouteSegment"/> touching the
    /// POI as <c>FromPoiId</c> OR <c>ToPoiId</c> (both directions, across all travel
    /// modes) whose <c>Fidelity</c> is NOT <see cref="Data.Entities.Fidelity.Manual"/>.
    /// Called when the POI's coordinates actually change so the affected legs
    /// recompute on the next trigger. A no-op when the POI has no cached rows.
    /// </summary>
    Task InvalidateForPoiAsync(int poiId, CancellationToken ct);

    /// <summary>
    /// Deletes the recompute-eligible cached rows backing the collection's current
    /// placeable legs — those whose <c>Fidelity</c> is
    /// <see cref="Data.Entities.Fidelity.Estimated"/>,
    /// <see cref="Data.Entities.Fidelity.Placeholder"/>, or the
    /// straight-line-fallback Estimated (eligible = NOT
    /// <see cref="Data.Entities.Fidelity.Manual"/> AND NOT
    /// <see cref="Data.Entities.Fidelity.Measured"/>). Used by the explicit
    /// Recompute action. Returns the number of rows deleted.
    /// </summary>
    Task<int> InvalidateRecomputableForCollectionAsync(int collectionId, CancellationToken ct);
}
