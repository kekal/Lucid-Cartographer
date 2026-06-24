using LucidCartographer.Data;
using LucidCartographer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LucidCartographer.Services.Trip;

/// <summary>
/// Default <see cref="IDistanceMatrixService"/>. Builds the N×N cost matrix using
/// mode-invariant haversine distance between every ordered pair of stops. Does not
/// read persisted <see cref="Data.Entities.TravelMode"/> or per-leg
/// <c>OutgoingTravelMode</c> because TSP-Sort must order stops before per-leg modes exist.
/// </summary>
public sealed class DistanceMatrixService(
    IDbContextFactory<AppDbContext> factory,
    IOptions<TravelTimeOptions> options) : IDistanceMatrixService
{
    // Retained for constructor shape compatibility; no longer used.
    private readonly TravelTimeOptions _options = options.Value;

    /// <inheritdoc />
    public async Task<DistanceMatrix?> BuildAsync(int collectionId, CancellationToken ct = default)
    {
        _ = _options;
        await using var db = await factory.CreateDbContextAsync(ct);

        // Routing candidate set: placeable, ordered stops in current Stop Order.
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

        // AD-1 critical guard (RD3): the TSP cost matrix uses RAW haversine distance and
        // must NEVER be routed through the smart-haversine detour factor
        // (TravelTimeOptions.DetourFactorFor). The detour factor lives only in the estimate
        // path (EstimatedTravelTime.Compute); applying it here would make stop ordering
        // vary with detour-factor config. Mode-invariant: TSP order is identical regardless
        // of travel mode AND of detour-factor configuration.
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
