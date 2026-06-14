using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services.Trip;

/// <summary>
/// TRIP-TRAVELTIME-01: the immutable result of a single provider leg lookup.
/// Canonical units (AR-11): <see cref="DurationSeconds"/> in seconds,
/// <see cref="DistanceMeters"/> in meters — no UI-edge conversion at this layer.
/// <see cref="Fidelity"/> is one of <see cref="Data.Entities.Fidelity"/>;
/// <see cref="GeometryPolyline"/> is null when the provider yields no road
/// geometry (the haversine Mock always does).
/// </summary>
public readonly record struct TravelLegResult(
    int DurationSeconds,
    double DistanceMeters,
    string Fidelity,
    string? GeometryPolyline);
