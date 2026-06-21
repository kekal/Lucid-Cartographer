namespace LucidCartographer.Data.Entities;

public class PoiCollectionItem
{
    public int PoiId { get; set; }
    public Poi Poi { get; set; } = null!;
    public int PoiCollectionId { get; set; }
    public PoiCollection PoiCollection { get; set; } = null!;

    // 1-based (1..N, contiguous, gap-free, unique per collection), stored exactly as displayed.
    public int OrderIndex { get; set; }

    // Per-Stop dwell time in MINUTES, stored on membership so same POI carries different dwell across Trips; null means zero.
    public int? DwellMinutes { get; set; }

    /// <summary>
    /// Travel mode for the leg leaving this stop. One of <see cref="TravelMode.All"/> (AnyAir, Drive, Walk, Cycle) or <c>null</c>;
    /// <c>null</c> is semantically identical to AnyAir (no separate "unset" sentinel).
    /// </summary>
    public string? OutgoingTravelMode { get; set; }
}
