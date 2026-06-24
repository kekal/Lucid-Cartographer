using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services.Trip;

/// <summary>
/// The single haversine straight-line travel-time estimate code path (DRY).
/// Reused by <see cref="MockTravelTimeProvider"/> (ground modes) and
/// <see cref="TravelTimeComputationBackgroundService"/> (provider fallback).
/// </summary>
internal static class EstimatedTravelTime
{
    /// <summary>
    /// Computes the smart-haversine Estimated travel time from <paramref name="from"/>
    /// to <paramref name="to"/>. The great-circle distance is scaled by the per-mode
    /// detour/winding factor (<see cref="TravelTimeOptions.DetourFactorFor"/>) to
    /// approximate road distance, then duration = adjusted distance ÷ mode speed
    /// (guarded against zero/negative speed to prevent misconfiguration errors).
    /// Any/Air uses a 1.0 factor (no winding).
    /// </summary>
    public static TravelLegResult Compute(
        TravelEndpoint from,
        TravelEndpoint to,
        string travelMode,
        TravelTimeOptions options)
    {
        var haversineMeters = GeoUtils.HaversineDistance(
            from.Latitude, from.Longitude, to.Latitude, to.Longitude);

        var adjustedMeters = haversineMeters * options.DetourFactorFor(travelMode);

        var speed = options.SpeedFor(travelMode);
        var seconds = speed > 0 ? (int)Math.Round(adjustedMeters / speed) : 0;

        return new TravelLegResult(
            DurationSeconds: seconds,
            DistanceMeters: adjustedMeters,
            Fidelity: Fidelity.Estimated,
            GeometryPolyline: null);
    }
}
