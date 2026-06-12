using System.ComponentModel.DataAnnotations.Schema;

namespace LucidCartographer.Data.Entities;

public class PoiCollection
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public required string Color { get; set; } = "#005bbf";

    public string? IconName { get; set; }

    public bool IsVisible { get; set; } = true;

    public DateTime CreatedDate { get; set; }

    public string? SourceType { get; set; } // Use CollectionSourceType constants

    public string? SourceFileName { get; set; }

    /// <summary>
    /// Not persisted — computed from DB at read time in GetCollectionsAsync.
    /// </summary>
    [NotMapped]
    public int PoiCount { get; set; }

    [System.ComponentModel.DataAnnotations.ConcurrencyCheck]
    public int Version { get; set; }

    // TRIP-SCHEMA-03: additive Trip-lens fields. A Collection is just a plain POI set
    // until Trip View is enabled; these persist the Trip arrangement per-Collection (D10).
    // FK ids only (no nav property) keeps the migration minimal and avoids extra navs on
    // Poi; the Start/Finish relationships are configured in AppDbContext with
    // OnDelete(SetNull) so deleting a Start/Finish POI nulls the reference, not cascades.
    // FinishPoiId == null ⇒ Roundtrip (closing leg returns to Start).

    /// <summary>Trip travel mode — one of <see cref="Entities.TravelMode"/>.</summary>
    public string TravelMode { get; set; } = Entities.TravelMode.AnyAir;

    /// <summary>Stop pinned to Order 1, or null. FK to Poi (no nav, OnDelete SetNull).</summary>
    public int? StartPoiId { get; set; }

    /// <summary>Stop pinned to Order N, or null ⇒ Roundtrip. FK to Poi (no nav, OnDelete SetNull).</summary>
    public int? FinishPoiId { get; set; }

    /// <summary>Optional wall-clock start time for the Trip; null ⇒ relative offsets only.</summary>
    public DateTime? TripStartTime { get; set; }

    /// <summary>Optional soft time budget in MINUTES; exceeding it raises a soft overrun flag.</summary>
    public int? TimeBudgetMinutes { get; set; }

    /// <summary>Whether Trip View is enabled for this Collection (per-Collection persistence — D10).</summary>
    public bool TripViewEnabled { get; set; }

    public List<PoiCollectionItem> CollectionItems { get; set; } = [];
}
