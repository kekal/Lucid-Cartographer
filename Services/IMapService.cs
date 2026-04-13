using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services
{
    public interface IMapService
    {
        Task InitMapAsync(string elementId);
        Task ShowCollectionAsync(int collectionId, List<Poi> pois, string color);
        Task HideCollectionAsync(int collectionId);
        Task FocusOnPoiAsync(double lat, double lon, int zoom = 16);
        Task FitBoundsAsync();
        Task RefreshLayoutAsync();
        Task HighlightMarkerAsync(int poiId);
        /// <summary>
        /// CRIT-04: Destroy the JS-side map object to prevent memory leaks on navigation.
        /// </summary>
        Task DestroyMapAsync();
        event Action<int>? OnMarkerClicked;
    }
}
