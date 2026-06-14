namespace LucidCartographer.Services.Trip;

/// <summary>
/// TRIP-TRAVELTIME-01: tunables for the travel-time slice, bound from the
/// <c>TravelTime</c> section of appsettings.json. A single assumed speed is the
/// only knob here — the per-mode speed table (AR-10) is a Story 2.2 concern and
/// is deliberately NOT modelled yet.
/// </summary>
public sealed class TravelTimeOptions
{
    /// <summary>
    /// Assumed straight-line travel speed in METERS PER SECOND, used by the
    /// haversine Mock to derive a duration from the great-circle distance.
    /// Default: 50 km/h ≈ 13.8889 m/s.
    /// </summary>
    public double AssumedSpeedMetersPerSecond { get; set; } = 50_000.0 / 3600.0;

    /// <summary>
    /// Idle poll interval in seconds when the compute queue is empty. The
    /// background service still wakes instantly on
    /// <see cref="TravelTimeTrigger.Signal"/>; this bounds the latency for legs
    /// whose cache rows go missing without a signal. Default: 30.
    /// </summary>
    public int IdlePollSeconds { get; set; } = 30;
}
