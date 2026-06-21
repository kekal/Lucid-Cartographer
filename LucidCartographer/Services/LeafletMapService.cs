using LucidCartographer.Data.Entities;
using Microsoft.JSInterop;

namespace LucidCartographer.Services;

/// <summary>
/// DTO for POI data passed to the JavaScript map interop layer.
/// </summary>
public record MarkerDto(int Id, string Name, double Latitude, double Longitude, string? Address, string? GoogleMapsUrl);

/// <summary>
/// DTO for a Trip leg: endpoint coordinates, measured flag, and optional encoded road geometry.
/// <c>GeometryPolyline</c> (when present) keys the JS render logic: solid line for measured routes, dashed for straight connectors.
/// </summary>
public record TripLegDto(
    double FromLat, double FromLon, double ToLat, double ToLon, bool IsMeasured, string? GeometryPolyline = null);

public class LeafletMapService(IJSRuntime js) : IMapService, IAsyncDisposable
{
    private DotNetObjectReference<LeafletMapService>? _dotnetRef;
    private int _disposed;
    // Guards JS cleanup: prevent interop calls during prerender scope teardown (would throw "JavaScript interop calls cannot be issued at this time").
    private int _initialized;

    public Func<int, Task>? OnMarkerClicked { get; set; }
    public Func<MapBounds, Task>? OnBoundsChanged { get; set; }

    public async Task InitMapAsync(string elementId)
    {
        _dotnetRef?.Dispose();
        _dotnetRef = DotNetObjectReference.Create(this);
        Interlocked.Exchange(ref _initialized, 1);
        await InvokeJsVoidAsync("leafletInterop.initMap", elementId, _dotnetRef);
    }

    public async Task ShowCollectionAsync(int collectionId, List<Poi> pois, string color)
    {
        // Filter unlocated POIs (null coords) before rendering.
        var dtos = pois
            .Where(p => p is { Latitude: not null, Longitude: not null })
            .Select(p => new MarkerDto(p.Id, p.Name, p.Latitude!.Value, p.Longitude!.Value, p.Address, p.GoogleMapsUrl))
            .ToArray();
        await InvokeJsVoidAsync("leafletInterop.addCollectionMarkers", collectionId, dtos, color);
    }

    public async Task HideCollectionAsync(int collectionId) => await InvokeJsVoidAsync("leafletInterop.removeCollectionMarkers", collectionId);

    public async Task FocusOnPoiAsync(double lat, double lon, int zoom = 16) => await InvokeJsVoidAsync("leafletInterop.focusOnPoi", lat, lon, zoom);

    public async Task FitBoundsAsync() => await InvokeJsVoidAsync("leafletInterop.fitBounds");

    public async Task RefreshLayoutAsync() => await InvokeJsVoidAsync("leafletInterop.invalidateSize");

    public async Task HighlightMarkerAsync(int poiId) => await InvokeJsVoidAsync("leafletInterop.highlightMarker", poiId);

    public async Task SetLabelsVisibleAsync(bool visible) => await InvokeJsVoidAsync("leafletInterop.setLabelsVisible", visible);

    public async Task SetStopOrdersAsync(IReadOnlyDictionary<int, int>? orders, TripMarkerRolesDto? roles = null) =>
        await InvokeJsVoidAsync("leafletInterop.setStopOrders", orders ?? new Dictionary<int, int>(), roles);

    public async Task DrawTripLegsAsync(IReadOnlyList<TripLegDto> legs, bool isRoundtrip = false) =>
        await InvokeJsVoidAsync("leafletInterop.drawTripLegs", legs, isRoundtrip);

    public async Task ClearTripAsync() => await InvokeJsVoidAsync("leafletInterop.clearTripLegs");

    public async Task SetRoutingAttributionAsync(string? html) =>
        await InvokeJsVoidAsync("leafletInterop.setRoutingAttribution", html);

    public async Task EmphasizeStopAsync(int? poiId) => await InvokeJsVoidAsync("leafletInterop.emphasizeStop", poiId);

    public async Task PanToStopAsync(int poiId) => await InvokeJsVoidAsync("leafletInterop.panToStop", poiId);

    public async Task DestroyMapAsync() => await InvokeJsVoidAsync("leafletInterop.destroyMap");

    public async Task EnableBoundsTrackingAsync() => await InvokeJsVoidAsync("leafletInterop.enableBoundsTracking");

    /// <summary>Internal: called from JavaScript only.</summary>
    [JSInvokable]
    public async Task OnMarkerClickedJs(int poiId)
    {
        if (OnMarkerClicked != null)
        {
            await OnMarkerClicked(poiId);
        }
    }

    /// <summary>Internal: called from JavaScript on moveend.</summary>
    [JSInvokable]
    public async Task OnBoundsChangedJs(MapBounds bounds)
    {
        if (OnBoundsChanged != null)
        {
            await OnBoundsChanged(bounds);
        }
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Skip JS cleanup if map was never initialized (guards against prerender teardown exceptions).
        if (Volatile.Read(ref _initialized) == 0)
        {
            _dotnetRef?.Dispose();
            _dotnetRef = null;
            return;
        }

        try
        {
            await js.InvokeVoidAsync("leafletInterop.destroyMap");
        }
        catch (Exception ex) when (IsCircuitGone(ex))
        {
            // Interop unavailable during disconnect/dispose.
        }

        _dotnetRef?.Dispose();
        _dotnetRef = null;
    }

    /// <summary>
    /// Helper to invoke JS interop with JSDisconnectedException/ObjectDisposedException handling.
    /// </summary>
    private async Task InvokeJsVoidAsync(string identifier, params object?[] args)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            await js.InvokeVoidAsync(identifier, args);
        }
        catch (Exception ex) when (IsCircuitGone(ex))
        {
            // Interop unavailable; will retry on next interactive pass.
        }
    }

    private static bool IsCircuitGone(Exception ex)
        => ex is JSDisconnectedException or ObjectDisposedException or InvalidOperationException;
}
