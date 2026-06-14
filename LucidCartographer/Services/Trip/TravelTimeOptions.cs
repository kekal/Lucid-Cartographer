namespace LucidCartographer.Services.Trip;

/// <summary>
/// TRIP-TRAVELTIME-01: tunables for the travel-time slice, bound from the
/// <c>TravelTime</c> section of appsettings.json. TRIP-TRAVELMODE-01 (Story 2.2,
/// AR-10): the single assumed speed becomes a per-mode table — the Any/Air speed
/// (<see cref="AssumedSpeedMetersPerSecond"/>, retained) plus a configurable
/// Drive/Walk/Cycle speed. All speeds are canonical METERS PER SECOND.
/// </summary>
public sealed class TravelTimeOptions
{
    /// <summary>
    /// Any/Air straight-line travel speed in METERS PER SECOND, used by the
    /// haversine Mock to derive a placeholder duration from the great-circle
    /// distance under <see cref="Data.Entities.TravelMode.AnyAir"/>.
    /// Default: 50 km/h ≈ 13.8889 m/s.
    /// </summary>
    public double AssumedSpeedMetersPerSecond { get; set; } = 50_000.0 / 3600.0;

    /// <summary>
    /// TRIP-TRAVELMODE-01: assumed Drive speed in METERS PER SECOND.
    /// Default: 50 km/h ≈ 13.8889 m/s.
    /// </summary>
    public double DriveSpeedMetersPerSecond { get; set; } = 50_000.0 / 3600.0;

    /// <summary>
    /// TRIP-TRAVELMODE-01: assumed Walk speed in METERS PER SECOND.
    /// Default: ~5 km/h ≈ 1.4 m/s.
    /// </summary>
    public double WalkSpeedMetersPerSecond { get; set; } = 1.4;

    /// <summary>
    /// TRIP-TRAVELMODE-01: assumed Cycle speed in METERS PER SECOND.
    /// Default: ~15 km/h ≈ 4.2 m/s.
    /// </summary>
    public double CycleSpeedMetersPerSecond { get; set; } = 4.2;

    /// <summary>
    /// Idle poll interval in seconds when the compute queue is empty. The
    /// background service still wakes instantly on
    /// <see cref="TravelTimeTrigger.Signal"/>; this bounds the latency for legs
    /// whose cache rows go missing without a signal. Default: 30.
    /// </summary>
    public int IdlePollSeconds { get; set; } = 30;

    /// <summary>
    /// TRIP-TRAVELMODE-01: resolves the assumed speed (m/s) for a travel mode.
    /// Any/Air falls through to <see cref="AssumedSpeedMetersPerSecond"/>, the
    /// same single speed Story 2.1 used for every mode.
    /// </summary>
    public double SpeedFor(string travelMode) => travelMode switch
    {
        Data.Entities.TravelMode.Drive => DriveSpeedMetersPerSecond,
        Data.Entities.TravelMode.Walk => WalkSpeedMetersPerSecond,
        Data.Entities.TravelMode.Cycle => CycleSpeedMetersPerSecond,
        _ => AssumedSpeedMetersPerSecond,
    };
}
