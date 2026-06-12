using LucidCartographer.Data.Entities;
using LucidCartographer.Services;

namespace LucidCartographer.Tests.Integration;

/// <summary>
/// No-op IMapService for integration tests where Leaflet JS is unavailable.
/// Prevents JS interop exceptions from breaking the Blazor Server circuit.
/// </summary>
public class StubMapService : IMapService
{
    // TRIP-STARTFINISH (Story 1.7) observability: integration tests can't see
    // Leaflet polylines (no real JS map), so the stub records the latest trip
    // overlay pushes. Static because the service is circuit-scoped while the
    // asserting test runs outside the circuit; the "Integration" xUnit
    // collection serializes test classes, so cross-test races are not a concern.
    // -1 = never drawn; ClearTripAsync records 0 (legs removed).
    public static int LastTripLegCount { get; private set; } = -1;
    public static bool LastTripLegsRoundtrip { get; private set; }
    public static TripMarkerRolesDto? LastTripMarkerRoles { get; private set; }

    public static void ResetTripRecording()
    {
        LastTripLegCount = -1;
        LastTripLegsRoundtrip = false;
        LastTripMarkerRoles = null;
    }

    public Func<int, Task>? OnMarkerClicked { get; set; }
    public Func<MapBounds, Task>? OnBoundsChanged { get; set; }

    public Task InitMapAsync(string elementId) => Task.CompletedTask;
    public Task ShowCollectionAsync(int collectionId, List<Poi> pois, string color) => Task.CompletedTask;
    public Task HideCollectionAsync(int collectionId) => Task.CompletedTask;
    public Task FocusOnPoiAsync(double lat, double lon, int zoom = 16) => Task.CompletedTask;
    public Task FitBoundsAsync() => Task.CompletedTask;
    public Task RefreshLayoutAsync() => Task.CompletedTask;
    public Task HighlightMarkerAsync(int poiId) => Task.CompletedTask;
    public Task SetLabelsVisibleAsync(bool visible) => Task.CompletedTask;

    public Task SetStopOrdersAsync(IReadOnlyDictionary<int, int>? orders, TripMarkerRolesDto? roles = null)
    {
        LastTripMarkerRoles = roles;
        return Task.CompletedTask;
    }

    public Task DrawTripLegsAsync(IReadOnlyList<TripLegDto> legs, bool isRoundtrip = false)
    {
        LastTripLegCount = legs.Count;
        LastTripLegsRoundtrip = isRoundtrip;
        return Task.CompletedTask;
    }

    public Task ClearTripAsync()
    {
        LastTripLegCount = 0;
        return Task.CompletedTask;
    }
    public Task EmphasizeStopAsync(int? poiId) => Task.CompletedTask;
    public Task PanToStopAsync(int poiId) => Task.CompletedTask;
    public Task DestroyMapAsync() => Task.CompletedTask;
    public Task EnableBoundsTrackingAsync() => Task.CompletedTask;
}