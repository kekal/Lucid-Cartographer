namespace LucidCartographer.Services.Trip;

/// <summary>
/// Tunables for the self-hosted Valhalla travel-time provider, bound from the
/// <c>TravelTime:Valhalla</c> section of appsettings.json. Unlike OSRM, a single
/// Valhalla engine serves every ground mode via dynamic costing, so there is one
/// base URL (not one per profile). Valhalla is self-hosted, so coordinates never egress.
/// </summary>
public sealed class ValhallaOptions
{
    /// <summary>
    /// Base URL of the Valhalla routing engine, e.g. <c>http://valhalla:8002</c>.
    /// One engine serves all ground modes (Drive/Walk/Cycle) via dynamic costing.
    /// </summary>
    public string BaseUrl { get; set; } = "http://valhalla:8002";

    /// <summary>
    /// Per-request HTTP timeout in seconds for a Valhalla <c>/route</c> call.
    /// Timeout exceptions degrade to Estimated. Default: 10.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Geometry encoding precision for the stored polyline. Valhalla emits polyline6,
    /// so this defaults to 6 and the JS decoder (<c>leafletInterop.js#decodePolyline</c>)
    /// MUST decode at the matching precision — factor <c>1e-6</c>, not <c>1e-5</c>.
    /// Treated as the contract the decoder must match, not a per-request toggle. Default: 6.
    /// </summary>
    public int GeometryPrecision { get; set; } = 6;

    /// <summary>
    /// Maps a ground travel mode to its Valhalla costing token (the <c>costing</c> field
    /// in the <c>/route</c> request body): Drive→auto, Walk→pedestrian, Cycle→bicycle.
    /// Returns null for Any/Air (never routed by Valhalla — the provider returns a
    /// Placeholder without any HTTP) or any other unsupported mode.
    /// </summary>
    public static string? CostingFor(string travelMode) => travelMode switch
    {
        Data.Entities.TravelMode.Drive => "auto",
        Data.Entities.TravelMode.Walk => "pedestrian",
        Data.Entities.TravelMode.Cycle => "bicycle",
        _ => null,
    };
}
