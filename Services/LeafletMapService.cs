using LucidCartographer.Data.Entities;
using Microsoft.JSInterop;

namespace LucidCartographer.Services;

public class LeafletMapService : IMapService, IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private DotNetObjectReference<LeafletMapService>? _dotnetRef;

    public event Action<int>? OnMarkerClicked;

    public LeafletMapService(IJSRuntime js)
    {
        _js = js;
    }

    public async Task InitMapAsync(string elementId)
    {
        _dotnetRef = DotNetObjectReference.Create(this);
        await _js.InvokeVoidAsync("leafletInterop.initMap", elementId, _dotnetRef);
    }

    public async Task ShowCollectionAsync(int collectionId, List<Poi> pois, string color)
    {
        var dtos = pois.Select(p => new {
            id = p.Id,
            name = p.Name,
            latitude = p.Latitude,
            longitude = p.Longitude,
            address = p.Address,
            googleMapsUrl = p.GoogleMapsUrl
        }).ToArray();
        await _js.InvokeVoidAsync("leafletInterop.addCollectionMarkers", collectionId, dtos, color);
    }

    public async Task HideCollectionAsync(int collectionId)
    {
        await _js.InvokeVoidAsync("leafletInterop.removeCollectionMarkers", collectionId);
    }

    public async Task FocusOnPoiAsync(double lat, double lon, int zoom = 16)
    {
        await _js.InvokeVoidAsync("leafletInterop.focusOnPoi", lat, lon, zoom);
    }

    public async Task FitBoundsAsync()
    {
        await _js.InvokeVoidAsync("leafletInterop.fitBounds");
    }

    public async Task InvalidateSizeAsync()
    {
        await _js.InvokeVoidAsync("leafletInterop.invalidateSize");
    }

    public async Task HighlightMarkerAsync(int poiId)
    {
        await _js.InvokeVoidAsync("leafletInterop.highlightMarker", poiId);
    }

    [JSInvokable]
    public Task OnMarkerClickedJs(int poiId)
    {
        OnMarkerClicked?.Invoke(poiId);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _dotnetRef?.Dispose();
    }
}
