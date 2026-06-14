namespace LucidCartographer.Services.Trip;

/// <summary>
/// TRIP-TRAVELTIME-01: a single active travel-time provider (AR-2). Given the
/// two endpoints of a leg and a travel mode, returns its duration / distance /
/// fidelity / optional road geometry. The shipping default is the haversine
/// <see cref="MockTravelTimeProvider"/> (config-selectable), requiring zero
/// routing infrastructure.
///
/// DEVIATION (layering): the story's Dev Notes name the parameters as the VM's
/// <c>TripStop</c> projection, but Services must not reference Components
/// (Component → ViewModel → Service → Data). The provider therefore takes a
/// layer-local <see cref="TravelEndpoint"/> carrying exactly the POI id +
/// coordinates a leg lookup needs, so both the background service (POIs) and any
/// future caller can supply it without an upward dependency.
/// </summary>
public interface ITravelTimeProvider
{
    /// <summary>The provider id written to the cache's <c>Source</c> column.</summary>
    string Source { get; }

    /// <summary>
    /// Computes the leg from <paramref name="from"/> to <paramref name="to"/>
    /// under <paramref name="travelMode"/>. Directional: A→B need not equal B→A.
    /// </summary>
    Task<TravelLegResult> GetLegAsync(
        TravelEndpoint from,
        TravelEndpoint to,
        string travelMode,
        CancellationToken ct);
}

/// <summary>
/// TRIP-TRAVELTIME-01: a leg endpoint — the POI id and its (placeable)
/// coordinates. Only placeable stops ever reach the provider, so the
/// coordinates are non-nullable here.
/// </summary>
public readonly record struct TravelEndpoint(int PoiId, double Latitude, double Longitude);
