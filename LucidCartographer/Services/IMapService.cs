using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services;

public record MapBounds(double South, double West, double North, double East)
{
    public bool Contains(double lat, double lon) =>
        lat >= South && lat <= North && lon >= West && lon <= East;
}

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
    /// </summary>
    Task SetStopOrdersAsync(IReadOnlyDictionary<int, int>? orders);

    /// <summary>
    /// Draw (or incrementally redraw) the Trip View connecting legs — straight,
    /// non-Measured (dashed + muted) polylines between consecutive Stops plus the
    /// Roundtrip closing leg. Replaces only the prior trip-leg layer; an empty
    /// list clears it. The numbered Stop markers are applied separately via
    /// <see cref="SetStopOrdersAsync"/>.
    /// </summary>
    Task DrawTripLegsAsync(IReadOnlyList<TripLegDto> legs);

    /// <summary>Remove the Trip View connecting legs (Trip View off / collection hide).</summary>
    Task ClearTripAsync();

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
    /// CRIT-04: Destroy the JS-side map object to prevent memory leaks on navigation.
    /// </summary>
    Task DestroyMapAsync();

    /// <summary>Enable JS-side moveend tracking; fires OnBoundsChanged on every pan/zoom.</summary>
    Task EnableBoundsTrackingAsync();

    /// <summary>
    /// Callback invoked when a map marker is clicked. The int parameter is the POI ID.
    /// Replaces the previous event Action&lt;int&gt; to avoid interface event coupling (REVIEW-12).
    /// </summary>
    Func<int, Task>? OnMarkerClicked { get; set; }

    /// <summary>Callback invoked when the map viewport changes (pan/zoom).</summary>
    Func<MapBounds, Task>? OnBoundsChanged { get; set; }
}