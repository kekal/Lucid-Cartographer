using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services.Trip;

/// <summary>
/// TRIP-DEGRADE-01 (Story 2.3): the single haversine straight-line travel-time
/// estimate code path (DRY). Computes a leg's duration/distance from the
/// great-circle distance between two endpoints (<see cref="GeoUtils.HaversineDistance"/>)
/// divided by the per-mode assumed speed (<see cref="TravelTimeOptions.SpeedFor"/>),
/// carrying <see cref="Fidelity.Estimated"/> and no road geometry.
///
/// Both consumers reuse this:
/// <list type="bullet">
/// <item><see cref="MockTravelTimeProvider"/> — the shipping provider's ground-mode
/// branch (Drive/Walk/Cycle). The Mock keeps its own Any/Air ⇒
/// <see cref="Fidelity.Placeholder"/> branch and does NOT route Any/Air here.</item>
/// <item><see cref="TravelTimeComputationBackgroundService"/> — the provider-down
/// fallback, which substitutes this estimate (badged
/// <c>Source = <see cref="TravelTimeSource.EstimatedFallback"/></c>) when the active
/// provider throws.</item>
/// </list>
/// Internal — exercised directly via <c>InternalsVisibleTo</c>.
/// </summary>
internal static class EstimatedTravelTime
{
    /// <summary>
    /// Computes the straight-line Estimated leg from <paramref name="from"/> to
    /// <paramref name="to"/> under <paramref name="travelMode"/>. The duration is
    /// distance ÷ the mode's assumed speed (guarded against a zero/negative speed
    /// so a misconfigured value can't divide by zero). Always
    /// <see cref="Fidelity.Estimated"/> with <c>GeometryPolyline = null</c>.
    /// </summary>
    public static TravelLegResult Compute(
        TravelEndpoint from,
        TravelEndpoint to,
        string travelMode,
        TravelTimeOptions options)
    {
        var meters = GeoUtils.HaversineDistance(
            from.Latitude, from.Longitude, to.Latitude, to.Longitude);

        var speed = options.SpeedFor(travelMode);
        var seconds = speed > 0 ? (int)Math.Round(meters / speed) : 0;

        return new TravelLegResult(
            DurationSeconds: seconds,
            DistanceMeters: meters,
            Fidelity: Fidelity.Estimated,
            GeometryPolyline: null);
    }
}
