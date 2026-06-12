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
}