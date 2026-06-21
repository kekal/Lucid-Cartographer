using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services;

/// <summary>
/// POI and collection CRUD service. Mutation methods throw <see cref="InvalidOperationException"/>
/// on not-found and <see cref="ArgumentException"/> on invalid input; query methods return null/empty.
/// </summary>
public interface IPoiService
{
    Task<IReadOnlyList<PoiCollection>> GetCollectionsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Poi>> GetPoisByCollectionAsync(int collectionId, CancellationToken cancellationToken = default);
    Task<Dictionary<int, List<Poi>>> GetVisiblePoisGroupedAsync(CancellationToken cancellationToken = default);
    Task<Poi?> GetPoiAsync(int poiId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Poi>> SearchAsync(string query, CancellationToken cancellationToken = default);
    Task<Dictionary<int, List<int>>> GetPoiCollectionMembershipsAsync(IEnumerable<int> poiIds, CancellationToken cancellationToken = default);
    Task<Dictionary<int, List<string>>> GetPoiCollectionNamesAsync(IEnumerable<int> poiIds, CancellationToken cancellationToken = default);
    /// <summary>
    /// Returns the number of POIs with enrichment failures pending manual reset.
    /// </summary>
    Task<int> GetFailedEnrichmentCountAsync(CancellationToken cancellationToken = default);

    Task<Poi> CreatePoiAsync(Poi poi, int collectionId, CancellationToken cancellationToken = default);
    Task AddPoiToCollectionAsync(int poiId, int collectionId, CancellationToken cancellationToken = default);
    Task RemovePoiFromCollectionAsync(int poiId, int collectionId, CancellationToken cancellationToken = default);
    Task ToggleVisibilityAsync(int collectionId, CancellationToken cancellationToken = default);
    Task UpdatePoiAsync(Poi poi, CancellationToken cancellationToken = default);
    Task DeleteCollectionAsync(int collectionId, CancellationToken cancellationToken = default);
    Task UpdateCollectionColorAsync(int collectionId, string color, CancellationToken cancellationToken = default);
    Task RenameCollectionAsync(int collectionId, string name, CancellationToken cancellationToken = default);
    Task<PoiCollection> CreateCollectionAsync(string name, CancellationToken cancellationToken = default);
    /// <summary>
    /// Resets enrichment failure tracking for non-enriched POIs so they can be retried.
    /// </summary>
    Task<int> ResetFailedEnrichmentAsync(CancellationToken cancellationToken = default);
    Task MarkPoiForReEnrichmentAsync(int poiId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Resets enrichment state for every POI in a collection so the BG service
    /// re-runs enrichment on all of them. Returns the count queued.
    /// </summary>
    Task<int> MarkCollectionForReEnrichmentAsync(int collectionId, CancellationToken cancellationToken = default);
    Task ReplacePoiGoogleMapsUrlAsync(int poiId, string googleMapsUrl, CancellationToken cancellationToken = default);
    /// <summary>
    /// Flags the given POIs as explicitly requesting background enrichment
    /// without resetting any other state. Used by pipelines (e.g. import) that
    /// add rows and then hand them off to the enrichment worker.
    /// </summary>
    Task<int> RequestEnrichmentAsync(IReadOnlyCollection<int> poiIds, CancellationToken cancellationToken = default);
}
