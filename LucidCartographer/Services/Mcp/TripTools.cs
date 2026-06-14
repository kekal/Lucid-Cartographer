using System.ComponentModel;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Trip;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace LucidCartographer.Services.Mcp;

/// <summary>
/// TRIP-MCP-01 (Story 3.2, AR-8/FR-16): MCP trip tools — read a collection's
/// ordered Stops + computed Legs, assign the Stop Order, set Start/Finish, and set
/// Dwell Time. Auto-discovered by <c>WithToolsFromAssembly()</c> and served by the
/// existing authenticated <c>/mcp</c> endpoint (LAN → API key → OAuth) — no new
/// unauthenticated surface. Every write delegates to <see cref="ITripOrderingService"/>
/// (the single 1-based <c>OrderIndex</c> writer, AR-11): an MCP-assigned order
/// persists identically to a manual drag and stays drag-editable. No business logic
/// lives here.
/// </summary>
[McpServerToolType]
public static class TripTools
{
    [McpServerTool(Name = "get_trip")]
    [Description(
        "Read a collection's trip: the ordered placeable Stops (1-based OrderIndex, " +
        "Start/Finish flags, optional dwell minutes) and the cached directional Legs " +
        "between consecutive Stops under the collection's Travel Mode. Durations are in " +
        "SECONDS, distances in METERS. A Leg with null duration is not computed yet. " +
        "Use this before assign_stop_order to see the current order and the leg costs.")]
    public static async Task<TripDto> GetTrip(
        ITripOrderingService ordering,
        IDbContextFactory<AppDbContext> dbFactory,
        [Description("The collection id.")] int collectionId,
        CancellationToken ct = default)
    {
        var stops = await ordering.GetPlaceableStopsAsync(collectionId, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var collection = await db.PoiCollections.AsNoTracking()
            .Where(c => c.Id == collectionId)
            .Select(c => new { c.TravelMode, c.StartPoiId, c.FinishPoiId })
            .FirstOrDefaultAsync(ct);
        var travelMode = collection?.TravelMode ?? TravelMode.AnyAir;

        var poiIds = stops.Select(s => s.PoiId).ToList();
        var names = await db.Pois.AsNoTracking()
            .Where(p => poiIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name })
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var dwell = await db.PoiCollectionItems.AsNoTracking()
            .Where(ci => ci.PoiCollectionId == collectionId && poiIds.Contains(ci.PoiId))
            .Select(ci => new { ci.PoiId, ci.DwellMinutes })
            .ToDictionaryAsync(ci => ci.PoiId, ci => ci.DwellMinutes, ct);

        var stopDtos = stops.Select(s => new TripStopDto(
            s.PoiId,
            names.TryGetValue(s.PoiId, out var n) ? n : $"POI {s.PoiId}",
            s.OrderIndex,
            collection?.StartPoiId == s.PoiId,
            collection?.FinishPoiId == s.PoiId,
            dwell.TryGetValue(s.PoiId, out var d) ? d : null)).ToList();

        // Directional consecutive pairs, plus the closing leg back to the first Stop
        // on a Roundtrip (no distinct Finish) — the same shape the UI draws.
        var legDtos = new List<TripLegDto>();
        if (stops.Count >= 2)
        {
            var cached = await db.RouteSegments.AsNoTracking()
                .Where(r => r.TravelMode == travelMode
                            && poiIds.Contains(r.FromPoiId) && poiIds.Contains(r.ToPoiId))
                .ToListAsync(ct);
            var byPair = cached
                .GroupBy(r => (r.FromPoiId, r.ToPoiId))
                .ToDictionary(g => g.Key, g => g.First());

            var pairs = new List<(int From, int To)>();
            for (var k = 0; k < stops.Count - 1; k++)
            {
                pairs.Add((stops[k].PoiId, stops[k + 1].PoiId));
            }
            var finishIsDistinct = collection?.FinishPoiId is { } fid
                && fid != stops[0].PoiId && stops.Any(s => s.PoiId == fid);
            if (!finishIsDistinct)
            {
                pairs.Add((stops[^1].PoiId, stops[0].PoiId));
            }

            foreach (var (from, to) in pairs)
            {
                byPair.TryGetValue((from, to), out var seg);
                legDtos.Add(new TripLegDto(from, to, seg?.DurationSeconds, seg?.DistanceMeters, seg?.Fidelity));
            }
        }

        return new TripDto(collectionId, travelMode, stopDtos, legDtos);
    }

    [McpServerTool(Name = "assign_stop_order")]
    [Description(
        "Assign the full Stop Order of a collection's trip. Pass the collection id and " +
        "the PoiIds in the exact visiting order you want — this must be ALL of the " +
        "collection's placeable Stops, each listed once (no unknown / unplaceable / " +
        "missing / duplicate id), or the call errors. A designated Start stays first and " +
        "a designated Finish stays last regardless of where you place them (set or clear " +
        "those with set_trip_start/set_trip_finish). The order persists exactly like a " +
        "manual drag and the user can still drag-edit it afterwards. Returns the trip.")]
    public static async Task<TripDto> AssignStopOrder(
        ITripOrderingService ordering,
        IDbContextFactory<AppDbContext> dbFactory,
        [Description("The collection id.")] int collectionId,
        [Description("All placeable Stop PoiIds in the desired visiting order.")] int[] orderedPoiIds,
        CancellationToken ct = default)
    {
        // int[] (not IReadOnlyList<int>) so the MCP input schema reliably emits an
        // array across clients; the service method accepts the IReadOnlyList<int> it implements.
        await ordering.AssignOrderAsync(collectionId, orderedPoiIds, ct);
        return await GetTrip(ordering, dbFactory, collectionId, ct);
    }

    [McpServerTool(Name = "set_trip_start")]
    [Description(
        "Designate a Stop as the trip's Start (pinned to Order 1). The POI must be a " +
        "placeable, ordered Stop of the collection. Errors if the POI is the current " +
        "Finish (a Stop cannot be both). Persists like a manual designation.")]
    public static async Task<TripDto> SetTripStart(
        ITripOrderingService ordering,
        IDbContextFactory<AppDbContext> dbFactory,
        [Description("The collection id.")] int collectionId,
        [Description("The Stop's POI id.")] int poiId,
        CancellationToken ct = default)
    {
        await ordering.SetStartAsync(collectionId, poiId, ct);
        return await GetTrip(ordering, dbFactory, collectionId, ct);
    }

    [McpServerTool(Name = "set_trip_finish")]
    [Description(
        "Designate a Stop as the trip's Finish (pinned to the last Order); this makes the " +
        "trip an open path (no closing leg). The POI must be a placeable, ordered Stop. " +
        "Errors if the POI is the current Start. Persists like a manual designation.")]
    public static async Task<TripDto> SetTripFinish(
        ITripOrderingService ordering,
        IDbContextFactory<AppDbContext> dbFactory,
        [Description("The collection id.")] int collectionId,
        [Description("The Stop's POI id.")] int poiId,
        CancellationToken ct = default)
    {
        await ordering.SetFinishAsync(collectionId, poiId, ct);
        return await GetTrip(ordering, dbFactory, collectionId, ct);
    }

    [McpServerTool(Name = "clear_trip_start")]
    [Description("Clear the trip's Start designation (no pinned first Stop). No-op if none is set.")]
    public static async Task<TripDto> ClearTripStart(
        ITripOrderingService ordering,
        IDbContextFactory<AppDbContext> dbFactory,
        [Description("The collection id.")] int collectionId,
        CancellationToken ct = default)
    {
        await ordering.ClearStartAsync(collectionId, ct);
        return await GetTrip(ordering, dbFactory, collectionId, ct);
    }

    [McpServerTool(Name = "clear_trip_finish")]
    [Description("Clear the trip's Finish designation, returning it to a Roundtrip (closing leg restored). No-op if none is set.")]
    public static async Task<TripDto> ClearTripFinish(
        ITripOrderingService ordering,
        IDbContextFactory<AppDbContext> dbFactory,
        [Description("The collection id.")] int collectionId,
        CancellationToken ct = default)
    {
        await ordering.ClearFinishAsync(collectionId, ct);
        return await GetTrip(ordering, dbFactory, collectionId, ct);
    }

    [McpServerTool(Name = "set_dwell_time")]
    [Description(
        "Set (or clear) the dwell time in MINUTES for a Stop — how long the visitor stays " +
        "there, added to the itinerary timeline. Pass minutes to set, or omit/null to " +
        "clear. Out-of-range values (negative or more than 60 days) are ignored. " +
        "Persists under the shared write lock. Returns the trip.")]
    public static async Task<TripDto> SetDwellTime(
        ITripOrderingService ordering,
        IDbContextFactory<AppDbContext> dbFactory,
        [Description("The collection id.")] int collectionId,
        [Description("The Stop's POI id.")] int poiId,
        [Description("Dwell minutes (0..86400). Omit or null to clear.")] int? minutes = null,
        CancellationToken ct = default)
    {
        await ordering.SetDwellMinutesAsync(collectionId, poiId, minutes, ct);
        return await GetTrip(ordering, dbFactory, collectionId, ct);
    }
}
