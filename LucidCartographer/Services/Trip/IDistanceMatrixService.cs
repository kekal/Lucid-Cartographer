namespace LucidCartographer.Services.Trip;

/// <summary>
/// Builds the on-demand N×N Distance Matrix for TSP-Sort, reading the shared
/// <see cref="Data.Entities.RouteSegment"/> cache (used by both Legs and matrix)
/// and filling uncached pairs with haversine estimates to ensure a complete, deterministic matrix.
/// </summary>
public interface IDistanceMatrixService
{
    /// <summary>
    /// Builds the duration matrix over the collection's <b>placeable</b> Stops
    /// (ordered by current <c>OrderIndex</c>) under its persisted
    /// <see cref="Data.Entities.PoiCollection.TravelMode"/>. Returns null when the
    /// collection has fewer than two placeable Stops (nothing to sort).
    /// </summary>
    Task<DistanceMatrix?> BuildAsync(int collectionId, CancellationToken ct = default);
}

/// <summary>
/// The on-demand Distance Matrix: the ordered placeable Stops plus an N×N matrix
/// of travel time in SECONDS (canonical unit, matching
/// <see cref="Data.Entities.RouteSegment.DurationSeconds"/>). Cell [i][j] is the
/// directional cost Stop i → Stop j; the diagonal is 0. <see cref="FromCache"/>[i][j]
/// records whether the cell came from a cached row (true) or the haversine estimate
/// (false) — diagnostic only.
/// </summary>
public sealed record DistanceMatrix(
    IReadOnlyList<PlaceableStop> Stops,
    double[][] DurationSeconds,
    bool[][] FromCache);
