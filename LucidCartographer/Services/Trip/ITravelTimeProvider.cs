namespace LucidCartographer.Services.Trip;

/// <summary>
/// A single active travel-time provider. Given the two endpoints of a leg and a
/// travel mode, returns its duration / distance / fidelity / optional road geometry.
/// The shipping default is the haversine <see cref="MockTravelTimeProvider"/>
/// (config-selectable), requiring zero routing infrastructure.
///
/// Services must not reference Components (Component → ViewModel → Service → Data).
/// The provider therefore takes a layer-local <see cref="TravelEndpoint"/> carrying
/// exactly the POI id and coordinates a leg lookup needs, so both the background
/// service and any future caller can supply it without an upward dependency.
/// </summary>
public interface ITravelTimeProvider
{
    /// <summary>The provider id written to the cache's <c>Source</c> column.</summary>
    string Source { get; }

    /// <summary>
    /// The routing-data attribution HTML this provider's data obliges the UI to display,
    /// or <c>null</c> when the data is not licence-bound. An OSM-based provider (Valhalla)
    /// returns the OSM/ODbL routing attribution; the haversine <see cref="MockTravelTimeProvider"/>
    /// returns <c>null</c>. The attribution lives with the provider so the data-licence
    /// obligation is declared where the data source is.
    /// </summary>
    string? Attribution { get; }

    /// <summary>
    /// Capability flag declaring whether this provider returns real road-network
    /// measurements. <c>true</c> means a measured leg (<see cref="Data.Entities.Fidelity.Measured"/>)
    /// from an actual routing engine; <c>false</c> means estimate-only output
    /// (<see cref="Data.Entities.Fidelity.Estimated"/>/<see cref="Data.Entities.Fidelity.Placeholder"/>),
    /// as the haversine <see cref="MockTravelTimeProvider"/> produces.
    ///
    /// The Epic 2 Story 2.3 background recompute trigger reads this to decide whether an
    /// existing Estimated/fallback cache row is <em>upgrade-eligible</em>: the broadened
    /// pending-leg arm is gated on this being <c>true</c> so a Mock-only deployment never
    /// re-churns its own estimates (AD-2).
    /// </summary>
    bool ProducesMeasuredFidelity { get; }

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
/// A leg endpoint — the POI id and its coordinates. Only placeable stops reach
/// the provider, so coordinates are non-nullable.
/// </summary>
public readonly record struct TravelEndpoint(int PoiId, double Latitude, double Longitude);
