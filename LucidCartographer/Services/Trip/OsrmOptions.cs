namespace LucidCartographer.Services.Trip;

/// <summary>
/// TRIP-OSRM-01 (Story 4.1): tunables for the optional self-hosted OSRM
/// travel-time provider, bound from the <c>TravelTime:Osrm</c> section of
/// appsettings.json. Mirrors <see cref="TravelTimeOptions"/> conventions
/// (sealed, mutable settable props, sensible defaults).
///
/// PER-PROFILE base URLs (AR-3): an <c>osrm-routed</c> backend process serves
/// exactly the ONE profile its extract was built with — a single base URL cannot
/// serve car + foot + bike. So Drive/Walk/Cycle each carry their own optional URL
/// (Drive→car, Walk→foot, Cycle→bike). A mode whose URL is left empty/unset is
/// treated as "no coverage": the provider throws for that mode and the existing
/// background-service degradation branch substitutes a straight-line Estimated
/// value (TRIP-DEGRADE-01, AC3).
///
/// NFR7: OSRM is self-hosted, so Stop coordinates never leave the deployment —
/// there is no third-party out-call and therefore no egress-consent guard is
/// required for this provider.
/// </summary>
public sealed class OsrmOptions
{
    /// <summary>
    /// Base URL of the OSRM <c>car</c>-profile backend (Drive mode), e.g.
    /// <c>http://osrm-car:5000</c>. Empty/null ⇒ Drive has no OSRM coverage and
    /// degrades to the haversine Estimated fallback (AC3).
    /// </summary>
    public string? DriveBaseUrl { get; set; }

    /// <summary>
    /// Base URL of the OSRM <c>foot</c>-profile backend (Walk mode). Empty/null ⇒
    /// Walk degrades to the haversine Estimated fallback (AC3).
    /// </summary>
    public string? WalkBaseUrl { get; set; }

    /// <summary>
    /// Base URL of the OSRM <c>bike</c>-profile backend (Cycle mode). Empty/null ⇒
    /// Cycle degrades to the haversine Estimated fallback (AC3).
    /// </summary>
    public string? CycleBaseUrl { get; set; }

    /// <summary>
    /// Per-request HTTP timeout in seconds for an OSRM <c>/route</c> call. A
    /// timeout surfaces as a thrown exception that the background-service catch
    /// degrades to Estimated (AC3). Default: 10.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Geometry encoding precision. The provider requests
    /// <c>geometries=polyline</c> (OSRM's encoded-polyline, precision 5) and
    /// stores that string verbatim in <see cref="Data.Entities.RouteSegment.GeometryPolyline"/>.
    /// Story 4.2's decoder MUST use the same precision. TRIP-OSRM-01: deliberate,
    /// documented deviation from AR-3's literal <c>geometries=geojson</c> — same
    /// road geometry, a far more compact storage shape that matches the field name
    /// and decodes natively in Leaflet. Default: 5.
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
