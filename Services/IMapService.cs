using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services;

public interface IMapService
{
    Task InitMapAsync(string elementId);
    Task ShowCollectionAsync(int collectionId, List<Poi> pois, string color);
    Task HideCollectionAsync(int collectionId);
    Task FocusOnPoiAsync(double lat, double lon, int zoom = 16);
    Task FitBoundsAsync();
    Task InvalidateSizeAsync();
    Task HighlightMarkerAsync(int poiId);
    event Action<int>? OnMarkerClicked;
}
