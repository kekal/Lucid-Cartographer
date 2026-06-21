using System.ComponentModel;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Trip;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace LucidCartographer.Services.Mcp;

/// <summary>
/// MCP trip tools — read a collection's ordered Stops and computed Legs, assign the Stop Order,
/// set Start/Finish, and set Dwell Time. All writes delegate to <see cref="ITripOrderingService"/>
/// (the sole writer of 1-based <c>OrderIndex</c>), ensuring MCP-assigned order persists like
/// a manual drag and stays drag-editable. No business logic lives here.
/// </summary>
[McpServerToolType]
public static class TripTools
{
    [McpServerTool(Name = "get_trip")]
    [Description(
        "Read a collection's trip: the ordered placeable Stops (1-based OrderIndex, " +
        "Start/Finish flags, optional dwell minutes) and the cached directional Legs " +
        "between consecutive Stops. Each Leg carries its OWN travelMode (set per-leg via " +
        "set_leg_travel_mode; AnyAir/Drive/Walk/Cycle) plus the cached time for that mode: " +
        "durations in SECONDS, distances in METERS. A Leg with null duration is not " +
        "computed yet (an Any/Air leg stays manual-only and shows no automatic time). " +
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
            .Select(c => new { c.StartPoiId, c.FinishPoiId })
            .FirstOrDefaultAsync(ct);

        var poiIds = stops.Select(s => s.PoiId).ToList();
        var names = await db.Pois.AsNoTracking()
            .Where(p => poiIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name })
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        // Pull each stop's OutgoingTravelMode (null ≡ AnyAir); the From-stop's mode is the leg's mode.
        var members = await db.PoiCollectionItems.AsNoTracking()
            .Where(ci => ci.PoiCollectionId == collectionId && poiIds.Contains(ci.PoiId))
            .Select(ci => new { ci.PoiId, ci.DwellMinutes, ci.OutgoingTravelMode })
            .ToListAsync(ct);
        var dwell = members.ToDictionary(m => m.PoiId, m => m.DwellMinutes);
        var modeByPoiId = members.ToDictionary(m => m.PoiId, m => m.OutgoingTravelMode);

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
            // Read cache across ALL modes and key by (From, To, Mode) tuple so each leg selects its own row.
            var cached = await db.RouteSegments.AsNoTracking()
                .Where(r => poiIds.Contains(r.FromPoiId) && poiIds.Contains(r.ToPoiId))
                .ToListAsync(ct);
            var byKey = cached
                .GroupBy(r => (r.FromPoiId, r.ToPoiId, r.TravelMode))
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
                // The leg's mode is the From-stop's OutgoingTravelMode (null ≡ AnyAir);
                // its cache row is selected by (From, To, legMode) — null when none.
                var legMode = (modeByPoiId.TryGetValue(from, out var m) ? m : null) ?? TravelMode.AnyAir;
                byKey.TryGetValue((from, to, legMode), out var seg);
                legDtos.Add(new TripLegDto(from, to, legMode, seg?.DurationSeconds, seg?.DistanceMeters, seg?.Fidelity));
            }
        }

        return new TripDto(collectionId, stopDtos, legDtos);
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
        // int[] for stable MCP schema emission across clients; service accepts IReadOnlyList<int>.
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

    [McpServerTool(Name = "set_leg_travel_mode")]
    [Description(
        "Set the travel mode for ONE leg of a collection's trip. The leg is identified " +
        "by its FROM-stop PoiId (the leg LEAVING that stop, mirroring set_dwell_time). " +
        "Valid modes: AnyAir, Drive, Walk, Cycle (any other value errors). A ground mode " +
        "(Drive/Walk/Cycle) gets an automatic Estimated/Measured time computed in the " +
        "background; AnyAir is manual-only (no automatic door-to-door time). Persists " +
        "under the shared write lock. Returns the trip.")]
    public static async Task<TripDto> SetLegTravelMode(
        ITripOrderingService ordering,
        IDbContextFactory<AppDbContext> dbFactory,
        TravelTimeTrigger travelTimeTrigger,
        [Description("The collection id.")] int collectionId,
        [Description("The leg's FROM-stop POI id.")] int fromPoiId,
        [Description("The travel mode: AnyAir, Drive, Walk, or Cycle.")] string travelMode,
        CancellationToken ct = default)
    {
        // Service (sole writer) validates; errors surface to MCP client as tool errors.
        await ordering.SetOutgoingTravelModeAsync(collectionId, fromPoiId, travelMode, ct);

        // Ground modes need background time compute; AnyAir is manual-only (no auto signal).
        if (travelMode != TravelMode.AnyAir)
        {
            travelTimeTrigger.Signal();
        }

        return await GetTrip(ordering, dbFactory, collectionId, ct);
    }
}
