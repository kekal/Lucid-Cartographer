using LucidCartographer.Data.Entities;
using Microsoft.Extensions.Options;

namespace LucidCartographer.Services.Trip;

/// <summary>
/// TRIP-TRAVELTIME-01: the shipping default travel-time provider (AR-2). Distance
/// is the great-circle (haversine) distance between the two endpoints via the
/// shared <see cref="GeoUtils.HaversineDistance"/> helper — no routing
/// infrastructure required. Duration is distance ÷ a per-mode assumed speed
/// (<see cref="TravelTimeOptions.SpeedFor"/>). The result carries no road geometry
/// (<c>GeometryPolyline = null</c>) and <see cref="Source"/> = "Mock".
/// TRIP-TRAVELMODE-01 (Story 2.2, AR-10): the fidelity is
/// <see cref="Fidelity.Placeholder"/> for <see cref="TravelMode.AnyAir"/> (the UI
/// shows "—" — never a real door-to-door time) and <see cref="Fidelity.Estimated"/>
/// for Drive/Walk/Cycle.
/// </summary>
public sealed class MockTravelTimeProvider(IOptions<TravelTimeOptions> options) : ITravelTimeProvider
{
    /// <summary>The provider id stamped onto the cache's <c>Source</c> column.</summary>
    public const string ProviderId = "Mock";

    public string Source => ProviderId;

    /// <summary>
    /// TRIP-OSRM-02 (Story 4.2, AC4): the haversine Mock is not OSM-derived, so it
    /// declares no routing attribution — the base OSM tile attribution is the only
    /// obligation under the default provider.
    /// </summary>
    public string? Attribution => null;

    public Task<TravelLegResult> GetLegAsync(
        TravelEndpoint from,
        TravelEndpoint to,
        string travelMode,
        CancellationToken ct)
    {
        // TRIP-DEGRADE-01 (Story 2.3): the ground-mode haversine→(seconds,meters)
        // math now lives in the shared EstimatedTravelTime helper so the Mock and
        // the provider-down fallback share one estimate code path (DRY).
        var estimate = EstimatedTravelTime.Compute(from, to, travelMode, options.Value);

        // TRIP-TRAVELMODE-01: Any/Air carries Placeholder (the UI shows "—" — a
        // straight-line air estimate is never presented as a real time; a manual
        // entry overrides it). A duration is still computed so the leg/total has a
        // value internally. Drive/Walk/Cycle stay Estimated (the helper's fidelity).
        // Any/Air is NEVER routed through the Estimated fallback (Story 2.3 keeps it
        // Placeholder); only the Mock re-badges the shared estimate here.
        var result = travelMode == Data.Entities.TravelMode.AnyAir
            ? estimate with { Fidelity = Data.Entities.Fidelity.Placeholder }
            : estimate;

        return Task.FromResult(result);
    }
}
