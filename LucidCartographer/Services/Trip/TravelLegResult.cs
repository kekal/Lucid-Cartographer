using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services.Trip;

/// <summary>
/// Immutable result of a provider leg lookup; units are seconds and meters (no UI conversion here).
/// <see cref="GeometryPolyline"/> is null when the provider yields no road geometry.
/// </summary>
public readonly record struct TravelLegResult(
    int DurationSeconds,
    double DistanceMeters,
    string Fidelity,
    string? GeometryPolyline);
