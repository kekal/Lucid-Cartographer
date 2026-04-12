using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services
{
    public interface IPoiService
    {
        Task<List<PoiCollection>> GetCollectionsAsync(CancellationToken cancellationToken = default);
        Task<List<Poi>> GetPoisByCollectionAsync(int collectionId, CancellationToken cancellationToken = default);
        Task<Dictionary<int, List<Poi>>> GetVisiblePoisGroupedAsync(CancellationToken cancellationToken = default);
        Task ToggleVisibilityAsync(int collectionId, CancellationToken cancellationToken = default);
        Task<Poi?> GetPoiAsync(int poiId, CancellationToken cancellationToken = default);
        Task UpdatePoiAsync(Poi poi, CancellationToken cancellationToken = default);
        Task DeleteCollectionAsync(int collectionId, CancellationToken cancellationToken = default);
        Task<List<Poi>> SearchAsync(string query, CancellationToken cancellationToken = default);
        Task UpdateCollectionColorAsync(int collectionId, string color, CancellationToken cancellationToken = default);
    }
}
