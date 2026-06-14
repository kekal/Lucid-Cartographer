namespace LucidCartographer.Services.Trip;

/// <summary>
/// TRIP-MATRIX-01 (Story 3.1, D11): builds the on-demand N×N Distance Matrix that
/// TSP-Sort consumes. Reads the SAME shared <see cref="Data.Entities.RouteSegment"/>
/// cache as the displayed Legs (one cache, two readers — no separate matrix table)
/// and fills any pair with no cached row with the shared haversine straight-line
/// estimate, so the matrix is always complete and the sort is deterministic.
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
