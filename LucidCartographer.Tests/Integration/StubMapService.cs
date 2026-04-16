using LucidCartographer.Data.Entities;
using LucidCartographer.Services;

namespace LucidCartographer.Tests.Integration
{
    /// <summary>
    /// No-op IMapService for integration tests where Leaflet JS is unavailable.
    /// Prevents JS interop exceptions from breaking the Blazor Server circuit.
    /// </summary>
    public class StubMapService : IMapService
    {
        public Func<int, Task>? OnMarkerClicked { get; set; }
        public Func<MapBounds, Task>? OnBoundsChanged { get; set; }

        public Task InitMapAsync(string elementId) => Task.CompletedTask;
        public Task ShowCollectionAsync(int collectionId, List<Poi> pois, string color) => Task.CompletedTask;
        public Task HideCollectionAsync(int collectionId) => Task.CompletedTask;
        public Task FocusOnPoiAsync(double lat, double lon, int zoom = 16) => Task.CompletedTask;
        public Task FitBoundsAsync() => Task.CompletedTask;
        public Task RefreshLayoutAsync() => Task.CompletedTask;
        public Task HighlightMarkerAsync(int poiId) => Task.CompletedTask;
        public Task DestroyMapAsync() => Task.CompletedTask;
        public Task EnableBoundsTrackingAsync() => Task.CompletedTask;
    }
}
