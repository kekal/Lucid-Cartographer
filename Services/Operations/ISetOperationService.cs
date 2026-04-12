using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services.Operations
{
    /// <summary>
    /// Defines set operations on POI collections (subtract, intersect, union, dedup).
    /// </summary>
    public interface ISetOperationService
    {
        /// <summary>
        /// Executes a set operation on one or two POI collections.
        /// </summary>
        /// <param name="operation">The set operation to perform.</param>
        /// <param name="collectionAId">The ID of collection A.</param>
        /// <param name="collectionBId">The ID of collection B (required for binary operations).</param>
        /// <param name="toleranceMeters">Maximum distance in meters for proximity matching.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The operation result containing matched POIs and metadata.</returns>
        Task<OperationResult> ExecuteAsync(
            SetOperation operation,
            int collectionAId,
            int? collectionBId,
            double toleranceMeters = PoiMatcher.DefaultToleranceMeters,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Saves an operation result as a new POI collection.
        /// </summary>
        /// <param name="pois">The POIs to save in the new collection.</param>
        /// <param name="name">Name for the new collection.</param>
        /// <param name="color">Hex color for the new collection.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The created collection entity.</returns>
        Task<PoiCollection> CommitResultAsync(
            List<Poi> pois,
            string name,
            string color = "#7c3aed",
            CancellationToken cancellationToken = default);
    }
}
