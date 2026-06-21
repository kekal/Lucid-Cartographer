using LucidCartographer.Data.Entities;
using Microsoft.Extensions.Options;

namespace LucidCartographer.Services.Trip;

/// <summary>
/// Default travel-time provider. Distance is the great-circle (haversine) distance
/// between endpoints via <see cref="GeoUtils.HaversineDistance"/>. Duration is
/// distance ÷ per-mode assumed speed (<see cref="TravelTimeOptions.SpeedFor"/>).
/// Fidelity is <see cref="Fidelity.Placeholder"/> for <see cref="TravelMode.AnyAir"/>
/// and <see cref="Fidelity.Estimated"/> for Drive/Walk/Cycle; no road geometry.
/// </summary>
public sealed class MockTravelTimeProvider(IOptions<TravelTimeOptions> options) : ITravelTimeProvider
{
    public const string ProviderId = "Mock";

    public string Source => ProviderId;

    /// <summary>Haversine mock is not OSM-derived; attribution is the responsibility of the base map layer.</summary>
    public string? Attribution => null;

    public Task<TravelLegResult> GetLegAsync(
        TravelEndpoint from,
        TravelEndpoint to,
        string travelMode,
        CancellationToken ct)
    {
        var estimate = EstimatedTravelTime.Compute(from, to, travelMode, options.Value);

        // Any/Air uses Placeholder fidelity (not a real door-to-door time); Drive/Walk/Cycle stay Estimated.
        var result = travelMode == Data.Entities.TravelMode.AnyAir
            ? estimate with { Fidelity = Data.Entities.Fidelity.Placeholder }
            : estimate;

        return Task.FromResult(result);
    }
}
