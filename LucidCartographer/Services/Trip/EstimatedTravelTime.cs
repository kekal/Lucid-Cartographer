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
    /// Computes straight-line Estimated travel time from <paramref name="from"/> to
    /// <paramref name="to"/>. Duration = distance ÷ mode speed (guarded against
    /// zero/negative speed to prevent misconfiguration errors).
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
