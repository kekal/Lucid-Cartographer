namespace LucidCartographer.Services.Trip;

/// <summary>
/// Tunables for travel-time computation, bound from the <c>TravelTime</c> section
/// of appsettings.json. Speeds are per-mode (Drive/Walk/Cycle) plus a shared
/// Any/Air speed, all in METERS PER SECOND.
/// </summary>
public sealed class TravelTimeOptions
{
    /// <summary>
    /// Any/Air speed in METERS PER SECOND (default: 50 km/h ≈ 13.8889 m/s).
    /// </summary>
    public double AssumedSpeedMetersPerSecond { get; set; } = 50_000.0 / 3600.0;

    /// <summary>
    /// Drive speed in METERS PER SECOND (default: 50 km/h ≈ 13.8889 m/s).
    /// </summary>
    public double DriveSpeedMetersPerSecond { get; set; } = 50_000.0 / 3600.0;

    /// <summary>
    /// Walk speed in METERS PER SECOND (default: ~5 km/h ≈ 1.4 m/s).
    /// </summary>
    public double WalkSpeedMetersPerSecond { get; set; } = 1.4;

    /// <summary>
    /// Cycle speed in METERS PER SECOND (default: ~15 km/h ≈ 4.2 m/s).
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
    /// Resolves the assumed speed (m/s) for a travel mode.
    /// Any/Air falls through to the shared speed.
    /// </summary>
    public double SpeedFor(string travelMode) => travelMode switch
    {
        Data.Entities.TravelMode.Drive => DriveSpeedMetersPerSecond,
        Data.Entities.TravelMode.Walk => WalkSpeedMetersPerSecond,
        Data.Entities.TravelMode.Cycle => CycleSpeedMetersPerSecond,
        _ => AssumedSpeedMetersPerSecond,
    };
}
