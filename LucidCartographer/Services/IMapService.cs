using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services;

public record MapBounds(double South, double West, double North, double East)
{
    public bool Contains(double lat, double lon) =>
        lat >= South && lat <= North && lon >= West && lon <= East;
}

/// <summary>
/// Start/Finish marker-role DTO for the JS interop layer. Carries which POI markers render the distinct Start/Finish glyph/ring and localized accessible names.
/// </summary>
public record TripMarkerRolesDto(int? StartPoiId, int? FinishPoiId, string StartAria, string FinishAria);

public interface IMapService
{
    Task InitMapAsync(string elementId);
    Task ShowCollectionAsync(int collectionId, List<Poi> pois, string color);
    Task HideCollectionAsync(int collectionId);
    Task FocusOnPoiAsync(double lat, double lon, int zoom = 16);
    Task FitBoundsAsync();
    Task RefreshLayoutAsync();
    Task HighlightMarkerAsync(int poiId);

    /// <summary>Show or hide permanent POI name labels next to every marker.</summary>
    Task SetLabelsVisibleAsync(bool visible);

    /// <summary>
    /// Apply Trip View Stop Order badges to markers. When <paramref name="orders"/>
    /// has entries, the matching POI markers render their Stop number; passing
    /// null or an empty map reverts every marker to its plain dot.
    /// <paramref name="roles"/> marks the pinned Start and Finish markers with their distinct glyph/ring and accessible name; null clears any prior role marking.
    /// </summary>
    Task SetStopOrdersAsync(IReadOnlyDictionary<int, int>? orders, TripMarkerRolesDto? roles = null);

    /// <summary>
    /// Draw (or incrementally redraw) the Trip View connecting legs — straight,
    /// non-Measured (dashed + muted) polylines between consecutive Stops plus the
    /// Roundtrip closing leg. Replaces only the prior trip-leg layer; an empty
    /// list clears it. The numbered Stop markers are applied separately via
    /// <see cref="SetStopOrdersAsync"/>. <paramref name="isRoundtrip"/>
    /// flags the Roundtrip shape so the interop can tag the closing leg.
    /// </summary>
    Task DrawTripLegsAsync(IReadOnlyList<TripLegDto> legs, bool isRoundtrip = false);

    /// <summary>Remove the Trip View connecting legs (Trip View off / collection hide).</summary>
    Task ClearTripAsync();

    /// <summary>
    /// Set the routing-data attribution on the map's attribution control. When <paramref name="html"/> is non-null (an
    /// OSM-based routing provider such as Valhalla is active) the OSM/ODbL routing
    /// attribution is added on top of the base OSM tile attribution; when null any prior routing attribution is removed.
    /// </summary>
    Task SetRoutingAttributionAsync(string? html);

    /// <summary>
    /// Emphasise the selected Trip Stop marker, or clear emphasis when
    /// <paramref name="poiId"/> is null. At most one marker is emphasised; the
    /// prior emphasis is removed. Additive to the existing marker popup/click —
    /// it does not change the marker-click channel.
    /// </summary>
    Task EmphasizeStopAsync(int? poiId);

    /// <summary>
    /// Pan the map so the selected Stop's marker is within the viewport, only
    /// when it is currently outside it; the zoom level is left unchanged.
    /// </summary>
    Task PanToStopAsync(int poiId);

    /// <summary>
    /// Destroy the JS-side map object to prevent memory leaks on navigation.
    /// </summary>
    Task DestroyMapAsync();

    /// <summary>Enable JS-side moveend tracking; fires OnBoundsChanged on every pan/zoom.</summary>
    Task EnableBoundsTrackingAsync();

    /// <summary>
    /// Callback invoked when a map marker is clicked. The int parameter is the POI ID.
    /// </summary>
    Func<int, Task>? OnMarkerClicked { get; set; }

    /// <summary>Callback invoked when the map viewport changes (pan/zoom).</summary>
    Func<MapBounds, Task>? OnBoundsChanged { get; set; }
}