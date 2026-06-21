namespace LucidCartographer.Components.Shared.Trip;

/// <summary>
/// Which surface initiated a Trip stop selection. Drives the directional sync
/// follow-up: a <see cref="List"/> selection pans the map to the marker; a
/// <see cref="Map"/> selection scrolls the stop row into view.
/// </summary>
public enum TripSelectionSource
{
    List,
    Map,
}

/// <summary>
/// The Start/Finish role a stop holds in the Trip. Derived per-stop by <see cref="TripViewModel.StopRole"/>
/// from the collection's <c>StartPoiId</c>/<c>FinishPoiId</c>; both surfaces use
/// it to pick the badge/marker glyph and the set/unset control state.
/// </summary>
public enum TripStopRole
{
    None,
    Start,
    Finish,
}

// Legs are straight skeletons computed on the fly by TripViewModel from the Stop Order.
// The geometry (FromLat..ToLon) is never persisted. Travel-time fields are read from
// the persisted RouteSegment cache when a row exists for the leg's (FromPoiId, ToPoiId,
// TravelMode) key — null until computed by the background service. Line-solidity
// (geometric-fidelity rule) keys off IsMeasured; the haversine Mock yields Estimated.

/// <summary>
/// One straight connecting leg between two consecutive placeable stops (or the
/// closing leg back to the Start on a Roundtrip).
/// <para>
/// <see cref="DurationSeconds"/> (seconds), <see cref="DistanceMeters"/> (meters) and
/// <see cref="Fidelity"/> are populated from the RouteSegment cache; all three are
/// <c>null</c> when no cache row exists yet. <see cref="IsMeasured"/> is derived: true
/// only when <see cref="Fidelity"/> equals <see cref="Data.Entities.Fidelity.Measured"/>.
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
    // True when this leg's backing RouteSegment was produced by the provider-down
    // straight-line fallback (Source == TravelTimeSource.EstimatedFallback).
    // Drives IsShowingApproximateEstimates — distinct from a normally-Estimated leg.
    bool IsFallback = false,
    // Measured road geometry as an encoded polyline (precision 5, from OsrmTravelTimeProvider).
    // Null/empty = no road geometry known (Estimated/Manual/Placeholder/Air or cache not
    // yet computed) ⇒ map draws straight dashed connector. Only Measured legs carry it;
    // its presence (not IsMeasured alone) makes the line render solid. Decoded JS-side.
    string? GeometryPolyline = null,
    // This leg's own travel mode — the From-stop's OutgoingTravelMode. A null value is
    // normalized to AnyAir (one single "Any/Air" state, never null). Cache lookup is by
    // (From, To, Mode) key, not trip-wide mode. Any/Air legs have no ground cache row
    // ⇒ null DurationSeconds ⇒ "—". Ground modes (Walk/Drive/Cycle) auto-compute.
    string Mode = Data.Entities.TravelMode.AnyAir);

/// <summary>
/// One ordered, placeable stop projected for the stop-list panel and the
/// numbered map markers. <see cref="OrderIndex"/> is 1-based.
/// <see cref="IsStart"/>/<see cref="IsFinish"/> reflect the collection's
/// explicit Start/Finish designation (read-only here).
/// </summary>
public sealed record TripStop(
    int OrderIndex,
    int PoiId,
    string Name,
    double Lat,
    double Lon,
    bool IsStart,
    bool IsFinish,
    // The mode of the leg leaving this stop toward the next. One of TravelMode.All or
    // null (null ≡ AnyAir). Read from PoiCollectionItem.OutgoingTravelMode and consumed
    // by BuildLegs to set each leg's Mode and pick its cache key.
    string? OutgoingTravelMode = null);

/// <summary>
/// One stop-list row over the FULL trip membership — placeable stops and
/// unplaceable POIs alike (never silently dropped from the list).
/// <see cref="DisplayOrder"/> is the user-facing routed Stop Order, computed
/// over the placeable subset only: contiguous 1..M for placeable rows, and
/// <c>null</c> for unplaceable rows (which render the "Not placeable"
/// treatment instead of an order badge, so the presented numbering never shows
/// a gap). The stored <c>OrderIndex</c> (owned by TripOrderingService) is not
/// affected — this is presentation state only.
/// <para>
/// <see cref="DwellMinutes"/> carries the per-membership dwell time in minutes
/// (<c>null</c> = unset). <see cref="Address"/>, <see cref="IsEnriched"/>,
/// <see cref="EnrichmentNeedsManualUrl"/>, and <see cref="GoogleMapsUrl"/> are
/// presentation fields read from the already-loaded <c>Poi</c> in the membership read.
/// </para>
/// </summary>
public sealed record TripStopRow(
    int? DisplayOrder,
    int PoiId,
    string Name,
    bool IsPlaceable,
    int? DwellMinutes = null,
    string? Address = null,
    bool IsEnriched = false,
    bool EnrichmentNeedsManualUrl = false,
    string? GoogleMapsUrl = null);
