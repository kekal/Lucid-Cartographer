namespace LucidCartographer.Services.Trip;

/// <summary>
/// TRIP-DEGRADE-01 (Story 2.3): the <c>Source</c> values stamped onto a
/// <see cref="Data.Entities.RouteSegment"/>'s provenance column. These distinguish
/// HOW a cached leg was produced so the VM can tell a *degraded* leg (the routing
/// engine was unreachable, so a straight-line haversine estimate was substituted)
/// apart from a *normally* estimated one.
/// <list type="bullet">
/// <item><see cref="Mock"/> — the shipping haversine provider computed the leg
/// normally (Estimated for ground modes, Placeholder for Any/Air).</item>
/// <item><see cref="Manual"/> — a user-entered time (Story 2.2).</item>
/// <item><see cref="EstimatedFallback"/> — the active provider failed for this
/// leg and the compute service substituted a haversine Estimated value (this
/// story). Drives <see cref="Components.Shared.Trip.TripViewModel.IsShowingApproximateEstimates"/>
/// and the honest "showing straight-line estimates" note.</item>
/// </list>
/// </summary>
public static class TravelTimeSource
{
    /// <summary>The shipping haversine provider id (see <see cref="MockTravelTimeProvider.ProviderId"/>).</summary>
    public const string Mock = "Mock";

    /// <summary>A user-entered manual leg time (Story 2.2).</summary>
    public const string Manual = "Manual";

    /// <summary>
    /// A degraded leg: the active provider failed and the compute service
    /// substituted a haversine <see cref="Data.Entities.Fidelity.Estimated"/> value.
    /// </summary>
    public const string EstimatedFallback = "EstimatedFallback";
}
