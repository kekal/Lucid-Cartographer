namespace LucidCartographer.Services.Trip;

/// <summary>
/// Stamped onto <see cref="Data.Entities.RouteSegment"/>'s provenance column to distinguish
/// how a cached leg was produced: either normally estimated, or degraded (fallback to haversine
/// when the routing engine fails).
/// </summary>
public static class TravelTimeSource
{
    /// <summary>Haversine provider (normally estimated values).</summary>
    public const string Mock = "Mock";

    /// <summary>User-entered manual leg time.</summary>
    public const string Manual = "Manual";

    /// <summary>
    /// Self-hosted Valhalla provider: measured leg from real road network with encoded geometry.
    /// Opt-in per deployment — never the default.
    /// </summary>
    public const string Valhalla = "Valhalla";

    /// <summary>Provider failed; haversine fallback substituted.</summary>
    public const string EstimatedFallback = "EstimatedFallback";
}
