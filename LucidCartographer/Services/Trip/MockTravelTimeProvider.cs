using LucidCartographer.Data.Entities;
using Microsoft.Extensions.Options;

namespace LucidCartographer.Services.Trip;

/// <summary>
/// TRIP-TRAVELTIME-01: the shipping default travel-time provider (AR-2). Distance
/// is the great-circle (haversine) distance between the two endpoints via the
/// shared <see cref="GeoUtils.HaversineDistance"/> helper — no routing
/// infrastructure required. Duration is distance ÷ a single configurable assumed
/// speed (<see cref="TravelTimeOptions.AssumedSpeedMetersPerSecond"/>). The
/// result is always <see cref="Fidelity.Estimated"/> with no road geometry
/// (<c>GeometryPolyline = null</c>), and <see cref="Source"/> = "Mock".
/// </summary>
public sealed class MockTravelTimeProvider(IOptions<TravelTimeOptions> options) : ITravelTimeProvider
{
    /// <summary>The provider id stamped onto the cache's <c>Source</c> column.</summary>
    public const string ProviderId = "Mock";

    public string Source => ProviderId;

    public Task<TravelLegResult> GetLegAsync(
        TravelEndpoint from,
        TravelEndpoint to,
        string travelMode,
        CancellationToken ct)
    {
        var meters = GeoUtils.HaversineDistance(
            from.Latitude, from.Longitude, to.Latitude, to.Longitude);

        // Single assumed speed for every mode in this story; the per-mode speed
        // table (AR-10) lands in Story 2.2. Guard against a zero/negative speed
        // so a misconfigured value can't divide by zero.
        var speed = options.Value.AssumedSpeedMetersPerSecond;
        var seconds = speed > 0 ? (int)Math.Round(meters / speed) : 0;

        var result = new TravelLegResult(
            DurationSeconds: seconds,
            DistanceMeters: meters,
            Fidelity: Data.Entities.Fidelity.Estimated,
            GeometryPolyline: null);

        return Task.FromResult(result);
    }
}
