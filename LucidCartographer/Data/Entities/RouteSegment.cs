using System.ComponentModel.DataAnnotations;

namespace LucidCartographer.Data.Entities;

/// <summary>
/// Cached travel-time result for a single Leg between two POIs under a Travel Mode — the
/// Distance-Matrix / Leg cache.
/// TRIP-CACHE-01: the key (FromPoiId, ToPoiId, TravelMode) is DIRECTIONAL — A→B and B→A
/// are distinct rows and the pair order is never collapsed. Canonical units: travel time
/// in SECONDS (<see cref="DurationSeconds"/>), distance in METERS (<see cref="DistanceMeters"/>).
/// This story only persists the shape; the cache-write/invalidation service arrives in Epic 2.
/// </summary>
public class RouteSegment
{
    /// <summary>Origin POI — part of the directional composite key.</summary>
    public int FromPoiId { get; set; }

    /// <summary>Destination POI — part of the directional composite key.</summary>
    public int ToPoiId { get; set; }

    /// <summary>Travel mode — one of <see cref="Entities.TravelMode"/>; part of the composite key.</summary>
    public string TravelMode { get; set; } = Entities.TravelMode.AnyAir;

    /// <summary>Travel time in SECONDS (canonical unit — no conversion at this layer).</summary>
    public int DurationSeconds { get; set; }

    /// <summary>Distance in METERS (canonical unit — no conversion at this layer).</summary>
    public double DistanceMeters { get; set; }

    /// <summary>Encoded road geometry, or null = no road geometry (dashed/muted render later).</summary>
    public string? GeometryPolyline { get; set; }

    /// <summary>Result provenance — one of <see cref="Entities.Fidelity"/>.</summary>
    public string Fidelity { get; set; } = Entities.Fidelity.Estimated;

    /// <summary>Provider/source identifier that produced this result.</summary>
    public string Source { get; set; } = "";

    /// <summary>UTC timestamp the result was computed.</summary>
    public DateTime ComputedAt { get; set; }

    // Optimistic-concurrency token, matching the [ConcurrencyCheck] int Version precedent on
    // Poi/PoiCollection. The AppDbContext SaveChanges override deliberately does NOT bump this
    // in this story — cache-write ownership (and Version bumping) lands with the Epic 2 service.
    [ConcurrencyCheck]
    public int Version { get; set; }
}
