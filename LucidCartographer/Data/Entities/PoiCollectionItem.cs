namespace LucidCartographer.Data.Entities;

public class PoiCollectionItem
{
    public int PoiId { get; set; }
    public Poi Poi { get; set; } = null!;
    public int PoiCollectionId { get; set; }
    public PoiCollection PoiCollection { get; set; } = null!;

    // TRIP-SCHEMA-02: Stop Order within a Trip. 1-based (1..N, contiguous, gap-free,
    // unique per collection) — stored exactly as displayed, no 0-based+offset.
    // This story only persists the column; seeding/compaction logic is owned by Story 1.2.
    // Existing rows are backfilled to a 1-based order in the AddTripPlanning migration.
    public int OrderIndex { get; set; }

    // TRIP-SCHEMA-02: per-Stop dwell time in MINUTES, stored on the membership so the
    // same POI carries a different dwell across Trips. Null ⇒ zero contribution.
    public int? DwellMinutes { get; set; }

    /// <summary>
    /// TRIP-LEGMODE-01: per-leg travel mode for the leg LEAVING this stop toward the
    /// next stop in Stop Order. One of <see cref="TravelMode.All"/> (AnyAir, Drive,
    /// Walk, Cycle) or <c>null</c>. <c>null</c> is semantically identical to AnyAir —
    /// one single "undefined / Any-Air" state; there is NO separate "unset" sentinel.
    /// Constrained by CK_PoiCollectionItem_OutgoingTravelMode (NULL or TravelMode.All).
    /// </summary>
    public string? OutgoingTravelMode { get; set; }
}
