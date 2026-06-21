namespace LucidCartographer.Data.Entities;

/// <summary>
/// String constants for Trip Travel Mode values, persisted as strings (human-readable DB values)
/// and mirrored in <see cref="PoiCategory"/>; stored properties are restricted by EF check constraint in AppDbContext.
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
    /// Returns true if the travel mode is one of the allowed values; null is invalid.
    /// </summary>
    public static bool IsValid(string? travelMode) =>
        travelMode is not null && All.Contains(travelMode);
}
