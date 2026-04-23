using LucidCartographer.Data.Entities;
using Microsoft.JSInterop;

namespace LucidCartographer.Services
{
    /// <summary>
    /// DTO for POI data passed to the JavaScript map interop layer (REVIEW-21).
    /// Documents the JS interop contract explicitly.
    /// </summary>
    public record MarkerDto(int Id, string Name, double Latitude, double Longitude, string? Address, string? GoogleMapsUrl);

    public class LeafletMapService : IMapService, IAsyncDisposable
    {
        private readonly IJSRuntime _js;
        private DotNetObjectReference<LeafletMapService>? _dotnetRef;
        // REVIEW-11: Thread-safe disposed flag using Interlocked
        private int _disposed;
        // Tracks whether the JS-side map was ever created. The service is scoped, so
        // an instance is constructed for every request (including the static prerender
        // pass that never reaches OnAfterRenderAsync). Gating DisposeAsync on this
        // prevents JS interop calls during prerender scope teardown, which would throw
        // "JavaScript interop calls cannot be issued at this time".
        private int _initialized;

        public Func<int, Task>? OnMarkerClicked { get; set; }
        public Func<MapBounds, Task>? OnBoundsChanged { get; set; }

        public LeafletMapService(IJSRuntime js)
        {
            _js = js;
        }

        public async Task InitMapAsync(string elementId)
        {
            // Dispose existing ref to prevent GC handle leak on re-init
            _dotnetRef?.Dispose();
            _dotnetRef = DotNetObjectReference.Create(this);
            Interlocked.Exchange(ref _initialized, 1);
            await InvokeJsVoidAsync("leafletInterop.initMap", elementId, _dotnetRef);
        }

        public async Task ShowCollectionAsync(int collectionId, List<Poi> pois, string color)
        {
            // REVIEW-21: Named DTO instead of anonymous type.
            // Unlocated POIs (NULL coords) have nothing to render; filter them out here.
            var dtos = pois
                .Where(p => p.Latitude.HasValue && p.Longitude.HasValue)
                .Select(p => new MarkerDto(p.Id, p.Name, p.Latitude!.Value, p.Longitude!.Value, p.Address, p.GoogleMapsUrl))
                .ToArray();
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

        public async Task RefreshLayoutAsync()
        {
            await InvokeJsVoidAsync("leafletInterop.invalidateSize");
        }

        public async Task HighlightMarkerAsync(int poiId)
        {
            await InvokeJsVoidAsync("leafletInterop.highlightMarker", poiId);
        }

        public async Task DestroyMapAsync()
        {
            await InvokeJsVoidAsync("leafletInterop.destroyMap");
        }

        public async Task EnableBoundsTrackingAsync()
        {
            await InvokeJsVoidAsync("leafletInterop.enableBoundsTracking");
        }

        /// <summary>Internal: called from JavaScript only.</summary>
        [JSInvokable]
        public async Task OnMarkerClickedJs(int poiId)
        {
            if (OnMarkerClicked != null)
                await OnMarkerClicked(poiId);
        }

        /// <summary>Internal: called from JavaScript on moveend.</summary>
        [JSInvokable]
        public async Task OnBoundsChangedJs(MapBounds bounds)
        {
            if (OnBoundsChanged != null)
                await OnBoundsChanged(bounds);
        }

        public async ValueTask DisposeAsync()
        {
            // REVIEW-22: GC.SuppressFinalize per IAsyncDisposable pattern
            GC.SuppressFinalize(this);

            // REVIEW-11: Thread-safe dispose using Interlocked
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            // Only attempt JS cleanup if the map was actually initialised on the JS
            // side. Without this guard, the static prerender pass — which constructs
            // the scoped service but never reaches OnAfterRenderAsync — would try to
            // invoke JS during scope disposal and throw InvalidOperationException
            // ("JavaScript interop calls cannot be issued at this time").
            if (Volatile.Read(ref _initialized) == 0)
            {
                _dotnetRef?.Dispose();
                _dotnetRef = null;
                return;
            }

            // Clean up the JS-side map instance
            try
            {
                await _js.InvokeVoidAsync("leafletInterop.destroyMap");
            }
            catch (Exception ex) when (IsCircuitGone(ex))
            {
                // Circuit disconnected or interop unavailable during teardown.
                // Browser-side lifecycle handles final cleanup.
            }

            _dotnetRef?.Dispose();
            _dotnetRef = null;
        }

        /// <summary>
        /// Helper to invoke JS interop with JSDisconnectedException/ObjectDisposedException handling.
        /// </summary>
        private async Task InvokeJsVoidAsync(string identifier, params object?[] args)
        {
            if (Volatile.Read(ref _disposed) != 0) return;

            try
            {
                await _js.InvokeVoidAsync(identifier, args);
            }
            catch (Exception ex) when (IsCircuitGone(ex))
            {
                // Interop unavailable during disconnect/dispose/prerender.
                // Post-prerender interactive pass will re-issue calls as needed.
            }
        }

        private static bool IsCircuitGone(Exception ex)
            => ex is JSDisconnectedException or ObjectDisposedException or InvalidOperationException;
    }
}
