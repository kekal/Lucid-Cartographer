namespace LucidCartographer.Data.Entities;

/// <summary>
/// String constants for Trip Travel Mode values.
/// TRIP-SCHEMA-01: persisted as strings (human-readable DB values), mirroring the
/// <see cref="PoiCategory"/> string-constant precedent rather than an int-backed enum.
/// The storing properties (PoiCollection.TravelMode, RouteSegment.TravelMode) are typed
/// <c>string</c> and are restricted to this set by an EF check constraint in AppDbContext.
/// </summary>
public static class TravelMode
{
    public const string AnyAir = "AnyAir";
    public const string Drive = "Drive";
    public const string Walk = "Walk";
    public const string Cycle = "Cycle";

    public static readonly IReadOnlyList<string> All =
    [
        AnyAir, Drive, Walk, Cycle
    ];

    /// <summary>
    /// Returns true if the travel mode is one of the allowed values.
    /// TravelMode is a non-nullable column, so null is treated as invalid.
    /// </summary>
    public static bool IsValid(string? travelMode) =>
        travelMode is not null && All.Contains(travelMode);
}
