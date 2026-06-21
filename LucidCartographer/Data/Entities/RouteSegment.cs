using System.ComponentModel.DataAnnotations;

namespace LucidCartographer.Data.Entities;

/// <summary>
/// Cached travel-time result for a single Leg between two POIs under a Travel Mode. The key
/// (FromPoiId, ToPoiId, TravelMode) is directional — A→B and B→A are distinct rows.
/// Canonical units: travel time in SECONDS, distance in METERS.
/// </summary>
public class RouteSegment
{
    /// <summary>Origin POI — part of the directional composite key.</summary>
    public int FromPoiId { get; set; }

    /// <summary>Destination POI — part of the directional composite key.</summary>
    public int ToPoiId { get; set; }

    /// <summary>Travel mode (one of <see cref="Entities.TravelMode"/>); part of the composite key.</summary>
    public string TravelMode { get; set; } = Entities.TravelMode.AnyAir;

    /// <summary>Travel time in SECONDS.</summary>
    public int DurationSeconds { get; set; }

    /// <summary>Distance in METERS.</summary>
    public double DistanceMeters { get; set; }

    /// <summary>Encoded road geometry; null = no geometry available.</summary>
    public string? GeometryPolyline { get; set; }

    /// <summary>Result provenance — one of <see cref="Entities.Fidelity"/>.</summary>
    public string Fidelity { get; set; } = Entities.Fidelity.Estimated;

    /// <summary>Provider/source identifier that produced this result.</summary>
    public string Source { get; set; } = "";

    /// <summary>UTC timestamp the result was computed.</summary>
    public DateTime ComputedAt { get; set; }

    // Optimistic-concurrency token. AppDbContext deliberately does not bump this.
    [ConcurrencyCheck]
    public int Version { get; set; }
}
