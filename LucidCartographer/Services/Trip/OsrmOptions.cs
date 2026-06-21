namespace LucidCartographer.Services.Trip;

/// <summary>
/// Tunables for the optional self-hosted OSRM travel-time provider, bound from the
/// <c>TravelTime:Osrm</c> section of appsettings.json. Each mode (Drive, Walk, Cycle)
/// has its own base URL because <c>osrm-routed</c> serves exactly one profile per instance.
/// Empty/unset URLs indicate no coverage; the background service falls back to haversine
/// Estimated. OSRM is self-hosted, so coordinates never egress.
/// </summary>
public sealed class OsrmOptions
{
    /// <summary>
    /// Base URL of the OSRM <c>car</c>-profile backend (Drive mode), e.g. <c>http://osrm-car:5000</c>.
    /// Empty/null ⇒ Drive degrades to haversine Estimated.
    /// </summary>
    public string? DriveBaseUrl { get; set; }

    /// <summary>
    /// Base URL of the OSRM <c>foot</c>-profile backend (Walk mode).
    /// Empty/null ⇒ Walk degrades to haversine Estimated.
    /// </summary>
    public string? WalkBaseUrl { get; set; }

    /// <summary>
    /// Base URL of the OSRM <c>bike</c>-profile backend (Cycle mode).
    /// Empty/null ⇒ Cycle degrades to haversine Estimated.
    /// </summary>
    public string? CycleBaseUrl { get; set; }

    /// <summary>
    /// Per-request HTTP timeout in seconds for an OSRM <c>/route</c> call.
    /// Timeout exceptions degrade to Estimated. Default: 10.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Geometry encoding precision. The provider requests <c>geometries=polyline</c> (precision 5)
    /// and stores it verbatim in <see cref="Data.Entities.RouteSegment.GeometryPolyline"/>.
    /// The decoder MUST use the same precision. More compact than GeoJSON; decodes natively in Leaflet. Default: 5.
    /// </summary>
    public int GeometryPrecision { get; set; } = 5;

    /// <summary>
    /// Resolves the per-profile base URL for a ground travel mode, or null when
    /// the mode has no configured OSRM coverage. Only Drive/Walk/Cycle reach OSRM;
    /// Any/Air never does (the provider returns a Placeholder without any HTTP).
    /// </summary>
    public string? BaseUrlFor(string travelMode) => travelMode switch
    {
        Data.Entities.TravelMode.Drive => NullIfBlank(DriveBaseUrl),
        Data.Entities.TravelMode.Walk => NullIfBlank(WalkBaseUrl),
        Data.Entities.TravelMode.Cycle => NullIfBlank(CycleBaseUrl),
        _ => null,
    };

    /// <summary>
    /// Maps a ground travel mode to its OSRM profile token (the path segment in
    /// <c>/route/v1/{profile}/...</c>): Drive→car, Walk→foot, Cycle→bike.
    /// Returns null for Any/Air (never routed by OSRM).
    /// </summary>
    public static string? ProfileFor(string travelMode) => travelMode switch
    {
        Data.Entities.TravelMode.Drive => "car",
        Data.Entities.TravelMode.Walk => "foot",
        Data.Entities.TravelMode.Cycle => "bike",
        _ => null,
    };

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
