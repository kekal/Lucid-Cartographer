using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services
{
    /// <summary>
    /// Service abstraction for POI and collection CRUD operations.
    /// <para>
    /// Error handling contract:
    /// - Mutation methods (Create, Update, Delete, Toggle) throw <see cref="InvalidOperationException"/>
    ///   when the target entity is not found, and <see cref="ArgumentException"/> on invalid input.
    /// - Query methods (Get, Search) return null or empty collections when no results are found.
    /// </para>
    /// </summary>
    public interface IPoiService
    {
        // --- Query methods (return null/empty on not-found) ---

        Task<IReadOnlyList<PoiCollection>> GetCollectionsAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<Poi>> GetPoisByCollectionAsync(int collectionId, CancellationToken cancellationToken = default);
        Task<Dictionary<int, List<Poi>>> GetVisiblePoisGroupedAsync(CancellationToken cancellationToken = default);
        Task<Poi?> GetPoiAsync(int poiId, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<Poi>> SearchAsync(string query, CancellationToken cancellationToken = default);
        Task<Dictionary<int, int>> GetPoiCollectionIdsAsync(IEnumerable<int> poiIds, CancellationToken cancellationToken = default);
        Task<Dictionary<int, List<string>>> GetPoiCollectionNamesAsync(IEnumerable<int> poiIds, CancellationToken cancellationToken = default);
        /// <summary>
        /// Returns the number of POIs with enrichment failures pending manual reset.
        /// </summary>
        Task<int> GetFailedEnrichmentCountAsync(CancellationToken cancellationToken = default);

        // --- Mutation methods (throw on not-found / invalid input) ---

        Task<Poi> CreatePoiAsync(Poi poi, int collectionId, CancellationToken cancellationToken = default);
        Task AddPoiToCollectionAsync(int poiId, int collectionId, CancellationToken cancellationToken = default);
        Task RemovePoiFromCollectionAsync(int poiId, int collectionId, CancellationToken cancellationToken = default);
        Task ToggleVisibilityAsync(int collectionId, CancellationToken cancellationToken = default);
        Task UpdatePoiAsync(Poi poi, CancellationToken cancellationToken = default);
        Task DeleteCollectionAsync(int collectionId, CancellationToken cancellationToken = default);
        Task UpdateCollectionColorAsync(int collectionId, string color, CancellationToken cancellationToken = default);
        Task<PoiCollection> CreateCollectionAsync(string name, CancellationToken cancellationToken = default);
        /// <summary>
        /// Resets enrichment failure tracking for non-enriched POIs so they can be retried.
        /// </summary>
        Task<int> ResetFailedEnrichmentAsync(CancellationToken cancellationToken = default);
    }
}
