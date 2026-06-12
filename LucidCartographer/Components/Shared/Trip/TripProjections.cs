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

// TRIP-LEG-01: Phase 1 — all legs straight + non-Measured. These immutable
// projections are computed on the fly by TripViewModel from the seeded Stop
// Order; nothing here is persisted (the RouteSegment cache is an Epic 2/4
// concern). The line-solidity = geometric-fidelity rule means a leg renders
// solid only when IsMeasured is true — and no leg is Measured in Phase 1, so
// every leg draws dashed + muted.

/// <summary>
/// One straight connecting leg between two consecutive placeable stops (or the
/// closing leg back to the Start on a Roundtrip). <see cref="IsMeasured"/> is
/// always <c>false</c> in Phase 1 — the road-routing provider arrives in Epic 2/4.
/// </summary>
public sealed record TripLeg(
    int FromPoiId,
    int ToPoiId,
    double FromLat,
    double FromLon,
    double ToLat,
    double ToLon,
    bool IsMeasured);

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
