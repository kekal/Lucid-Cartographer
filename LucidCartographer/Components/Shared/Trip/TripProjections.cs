namespace LucidCartographer.Components.Shared.Trip;

/// <summary>
/// Which surface initiated a Trip stop selection. Drives the directional sync
/// follow-up: a <see cref="List"/> selection pans the map to the marker; a
/// <see cref="Map"/> selection scrolls the stop row into view (TRIP-SELECT-01).
/// </summary>
public enum TripSelectionSource
{
    List,
    Map,
}

/// <summary>
/// The Start/Finish role a stop holds in the Trip (Story 1.7,
/// [TRIP-STARTFINISH-01]). Derived per-stop by <see cref="TripViewModel.StopRole"/>
/// from the collection's <c>StartPoiId</c>/<c>FinishPoiId</c>; both surfaces use
/// it to pick the badge/marker glyph and the set/unset control state.
/// </summary>
public enum TripStopRole
{
    None,
    Start,
    Finish,
}

// TRIP-LEG-01: legs are straight skeletons computed on the fly by TripViewModel
// from the seeded Stop Order. The geometry (FromLat..ToLon) is never persisted.
// TRIP-TRAVELTIME-01 (Epic 2): the travel-time fields below ARE read back from
// the persisted RouteSegment cache when a row exists for the leg's
// (FromPoiId, ToPoiId, collection TravelMode) key — null until the background
// service has computed it (render "—" + computing). The line-solidity =
// geometric-fidelity rule still keys off IsMeasured (Measured fidelity only);
// the haversine Mock yields Estimated, so legs stay dashed + muted for now.

/// <summary>
/// One straight connecting leg between two consecutive placeable stops (or the
/// closing leg back to the Start on a Roundtrip).
/// <para>
/// TRIP-TRAVELTIME-01: <see cref="DurationSeconds"/> (canonical seconds),
/// <see cref="DistanceMeters"/> (canonical meters) and <see cref="Fidelity"/>
/// are populated from the RouteSegment cache; all three are <c>null</c> when no
/// cache row exists yet (not-yet-computed ⇒ the UI renders an em-dash + computing
/// state). <see cref="IsMeasured"/> is derived: true only when
/// <see cref="Fidelity"/> equals <see cref="Data.Entities.Fidelity.Measured"/>.
/// </para>
/// </summary>
public sealed record TripLeg(
    int FromPoiId,
    int ToPoiId,
    double FromLat,
    double FromLon,
    double ToLat,
    double ToLon,
    bool IsMeasured,
    int? DurationSeconds = null,
    double? DistanceMeters = null,
    string? Fidelity = null,
    // TRIP-DEGRADE-01 (Story 2.3): true when this leg's backing RouteSegment was
    // produced by the provider-down straight-line fallback (Source ==
    // TravelTimeSource.EstimatedFallback). Drives
    // TripViewModel.IsShowingApproximateEstimates and the honest "showing
    // straight-line estimates" note — distinct from a normally-Estimated Mock leg.
    bool IsFallback = false);

/// <summary>
/// One ordered, placeable stop projected for the stop-list panel and the
/// numbered map markers. <see cref="OrderIndex"/> is 1-based (AR-11).
/// <see cref="IsStart"/>/<see cref="IsFinish"/> reflect the collection's
/// explicit Start/Finish designation (read-only here; the controls that change
/// them belong to Story 1.7).
/// </summary>
public sealed record TripStop(
    int OrderIndex,
    int PoiId,
    string Name,
    double Lat,
    double Lon,
    bool IsStart,
    bool IsFinish);

/// <summary>
/// One stop-list row over the FULL trip membership — placeable stops and
/// unplaceable POIs alike (an unplaceable POI is never silently dropped from
/// the list; UX-DR10). [TRIP-PLACE-04][TRIP-ORDER-UNPLACE-01]
/// <see cref="DisplayOrder"/> is the user-facing routed Stop Order, computed
/// over the placeable subset only: contiguous 1..M for placeable rows, and
/// <c>null</c> for unplaceable rows (which render the "Not placeable"
/// treatment instead of an order badge, so the presented numbering never shows
/// a gap). The stored <c>OrderIndex</c> (owned by TripOrderingService) is not
/// affected — this is presentation state only. The row components render this
/// state verbatim; the placeability decision is made in TripViewModel.
/// <para>
/// TRIP-DWELL-01 (Story 2.5): <see cref="DwellMinutes"/> carries the per-membership
/// dwell time in canonical minutes (<c>null</c> = unset, contributes zero) read from
/// <c>PoiCollectionItem.DwellMinutes</c>. Present on placeable and unplaceable rows
/// alike; presentation only — the timeline that consumes it is Story 2.6.
/// </para>
/// </summary>
public sealed record TripStopRow(
    int? DisplayOrder,
    int PoiId,
    string Name,
    bool IsPlaceable,
    int? DwellMinutes = null);
