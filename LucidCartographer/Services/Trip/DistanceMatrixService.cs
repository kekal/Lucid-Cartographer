using LucidCartographer.Data;
using LucidCartographer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LucidCartographer.Services.Trip;

/// <summary>
/// TRIP-MATRIX-01 (Story 3.1, D11) / RD3 (Story 3.3): default
/// <see cref="IDistanceMatrixService"/>. Builds the N×N cost matrix from a single
/// MODE-INVARIANT basis — the straight-line / haversine distance between every
/// ordered pair of placeable Stops ([TRIP-CACHE-01]: A→B and B→A are distinct
/// cells, though haversine distance is symmetric). The matrix deliberately does
/// NOT read the collection's persisted <see cref="Data.Entities.TravelMode"/> nor
/// any per-leg <c>OutgoingTravelMode</c>: TSP-Sort must order Stops before per-leg
/// modes exist (the chicken-and-egg of Story 3.3), and under <c>Mock</c> the
/// optimal order is identical regardless of mode (time = distance × a monotone
/// speed scalar), so haversine distance yields the same ordering as any mode would.
///
/// SCOPE: read-only input to TSP-Sort. The matrix never writes back to the cache —
/// the background compute service owns cache writes for the actual displayed legs.
/// Because the basis is computed straight-line distance (never a cached duration),
/// <see cref="DistanceMatrix.FromCache"/> is always all-false: there are no cached
/// rows feeding the cost matrix any more.
/// </summary>
public sealed class DistanceMatrixService(
    IDbContextFactory<AppDbContext> factory,
    IOptions<TravelTimeOptions> options) : IDistanceMatrixService
{
    // Retained for ctor-shape compatibility (no new ctor dependency, NFR10). The
    // mode-invariant haversine basis no longer needs any per-mode speed scalar.
    private readonly TravelTimeOptions _options = options.Value;

    /// <inheritdoc />
    public async Task<DistanceMatrix?> BuildAsync(int collectionId, CancellationToken ct = default)
    {
        _ = _options;
        await using var db = await factory.CreateDbContextAsync(ct);

        // The routing candidate set: placeable, ordered Stops only ([TRIP-PLACE-03]),
        // in current Stop Order. Coordinates are non-null by the placeability filter.
        var members = await db.PoiCollectionItems
            .AsNoTracking()
            .Where(ci => ci.PoiCollectionId == collectionId && ci.OrderIndex > 0)
            .Select(ci => new { ci.PoiId, ci.OrderIndex, ci.Poi.Latitude, ci.Poi.Longitude })
            .ToListAsync(ct);

        var stops = members
            .Where(m => StopPlaceability.IsPlaceable(m.Latitude, m.Longitude))
            .OrderBy(m => m.OrderIndex)
            .Select(m => new PlaceableStop(m.PoiId, m.OrderIndex, m.Latitude!.Value, m.Longitude!.Value))
            .ToList();

        if (stops.Count < 2)
        {
            return null;
        }

        // RD3: the cost matrix is the mode-invariant haversine straight-line
        // distance for every pair. No TravelMode is read, no RouteSegment cache is
        // filtered, no per-leg OutgoingTravelMode is consulted — so the matrix (and
        // therefore the TSP order) is identical regardless of any mode.
        var n = stops.Count;
        var matrix = new double[n][];
        var fromCache = new bool[n][];
        for (var i = 0; i < n; i++)
        {
            matrix[i] = new double[n];
            fromCache[i] = new bool[n]; // always false: distances are computed, not cached
            for (var j = 0; j < n; j++)
            {
                if (i == j)
                {
                    continue; // diagonal stays 0 — never routed
                }

                matrix[i][j] = GeoUtils.HaversineDistance(
                    stops[i].Latitude, stops[i].Longitude,
                    stops[j].Latitude, stops[j].Longitude);
            }
        }

        return new DistanceMatrix(stops, matrix, fromCache);
    }
}
