using LucidCartographer.Data.Entities;
using Microsoft.JSInterop;

namespace LucidCartographer.Services
{
    public class LeafletMapService : IMapService, IAsyncDisposable
    {
        private readonly IJSRuntime _js;
        private DotNetObjectReference<LeafletMapService>? _dotnetRef;
        private bool _disposed;

        public event Action<int>? OnMarkerClicked;

        public LeafletMapService(IJSRuntime js)
        {
            _js = js;
        }

        public async Task InitMapAsync(string elementId)
        {
            // Dispose existing ref to prevent GC handle leak on re-init
            _dotnetRef?.Dispose();
            _dotnetRef = DotNetObjectReference.Create(this);
            await InvokeJsVoidAsync("leafletInterop.initMap", elementId, _dotnetRef);
        }

        public async Task ShowCollectionAsync(int collectionId, List<Poi> pois, string color)
        {
            var dtos = pois.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                latitude = p.Latitude,
                longitude = p.Longitude,
                address = p.Address,
                googleMapsUrl = p.GoogleMapsUrl
            }).ToArray();
            await InvokeJsVoidAsync("leafletInterop.addCollectionMarkers", collectionId, dtos, color);
        }

        public async Task HideCollectionAsync(int collectionId)
        {
            await InvokeJsVoidAsync("leafletInterop.removeCollectionMarkers", collectionId);
        }

        public async Task FocusOnPoiAsync(double lat, double lon, int zoom = 16)
        {
            await InvokeJsVoidAsync("leafletInterop.focusOnPoi", lat, lon, zoom);
        }

        public async Task FitBoundsAsync()
        {
            await InvokeJsVoidAsync("leafletInterop.fitBounds");
        }

        public async Task InvalidateSizeAsync()
        {
            await InvokeJsVoidAsync("leafletInterop.invalidateSize");
        }

        public async Task HighlightMarkerAsync(int poiId)
        {
            await InvokeJsVoidAsync("leafletInterop.highlightMarker", poiId);
        }

        /// <summary>Internal: called from JavaScript only.</summary>
        [JSInvokable]
        public Task OnMarkerClickedJs(int poiId)
        {
            OnMarkerClicked?.Invoke(poiId);
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            // Clean up the JS-side map instance
            try
            {
                await _js.InvokeVoidAsync("leafletInterop.destroyMap");
            }
            catch (JSDisconnectedException)
            {
                // Circuit already disconnected, nothing to clean up on JS side
            }
            catch (ObjectDisposedException)
            {
                // JS runtime already disposed
            }

            _dotnetRef?.Dispose();
            _dotnetRef = null;
        }

        /// <summary>
        /// Helper to invoke JS interop with JSDisconnectedException/ObjectDisposedException handling.
        /// </summary>
        private async Task InvokeJsVoidAsync(string identifier, params object?[] args)
        {
            if (_disposed) return;

            try
            {
                await _js.InvokeVoidAsync(identifier, args);
            }
            catch (JSDisconnectedException)
            {
                // Circuit disconnected — silently ignore since the browser tab is gone
            }
            catch (ObjectDisposedException)
            {
                // JS runtime disposed — component is being torn down
            }
        }
    }
}
