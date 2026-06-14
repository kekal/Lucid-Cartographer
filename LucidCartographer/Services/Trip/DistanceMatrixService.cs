using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LucidCartographer.Services.Trip;

/// <summary>
/// TRIP-MATRIX-01 (Story 3.1, D11): default <see cref="IDistanceMatrixService"/>.
/// Builds the N×N matrix from the shared <see cref="RouteSegment"/> cache, reusing
/// every cached pair under the collection's persisted <see cref="TravelMode"/>
/// directionally ([TRIP-CACHE-01]: A→B and B→A are distinct cells). Any pair with
/// no cached row is filled with <see cref="EstimatedTravelTime"/>'s haversine
/// straight-line value — the same code path the provider-down fallback uses — so
/// the matrix is always complete.
///
/// SCOPE: read-only input to TSP-Sort. The matrix NEVER writes the estimated fill
/// values back to the cache — the background compute service owns cache writes for
/// the actual displayed legs (a warm cache where computed, an estimate where not).
/// </summary>
public sealed class DistanceMatrixService(
    IDbContextFactory<AppDbContext> factory,
    IOptions<TravelTimeOptions> options) : IDistanceMatrixService
{
    private readonly TravelTimeOptions _options = options.Value;

    /// <inheritdoc />
    public async Task<DistanceMatrix?> BuildAsync(int collectionId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var travelMode = await db.PoiCollections
            .AsNoTracking()
            .Where(c => c.Id == collectionId)
            .Select(c => c.TravelMode)
            .FirstOrDefaultAsync(ct) ?? TravelMode.AnyAir;

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

        var poiIds = stops.Select(s => s.PoiId).ToHashSet();

        // Cached durations among these Stops under this mode, keyed directionally.
        var cached = await db.RouteSegments
            .AsNoTracking()
            .Where(r => r.TravelMode == travelMode
                        && poiIds.Contains(r.FromPoiId)
                        && poiIds.Contains(r.ToPoiId))
            .Select(r => new { r.FromPoiId, r.ToPoiId, r.DurationSeconds })
            .ToListAsync(ct);
        var cache = cached.ToDictionary(r => (r.FromPoiId, r.ToPoiId), r => r.DurationSeconds);

        var n = stops.Count;
        var matrix = new double[n][];
        var fromCache = new bool[n][];
        for (var i = 0; i < n; i++)
        {
            matrix[i] = new double[n];
            fromCache[i] = new bool[n];
            for (var j = 0; j < n; j++)
            {
                if (i == j)
                {
                    continue; // diagonal stays 0 — never routed
                }

                if (cache.TryGetValue((stops[i].PoiId, stops[j].PoiId), out var seconds))
                {
                    matrix[i][j] = seconds;
                    fromCache[i][j] = true;
                }
                else
                {
                    // No cached row for this pair: fill with the shared haversine
                    // estimate (NOT written back to the cache).
                    var from = new TravelEndpoint(stops[i].PoiId, stops[i].Latitude, stops[i].Longitude);
                    var to = new TravelEndpoint(stops[j].PoiId, stops[j].Latitude, stops[j].Longitude);
                    matrix[i][j] = EstimatedTravelTime.Compute(from, to, travelMode, _options).DurationSeconds;
                }
            }
        }

        return new DistanceMatrix(stops, matrix, fromCache);
    }
}
