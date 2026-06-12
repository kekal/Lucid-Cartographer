namespace LucidCartographer.Data.Entities;

/// <summary>
/// String constants for Leg travel-time Fidelity values.
/// TRIP-SCHEMA-01: persisted as strings (human-readable DB values), mirroring the
/// <see cref="PoiCategory"/> string-constant precedent rather than an int-backed enum.
/// The storing property (RouteSegment.Fidelity) is typed <c>string</c> and is restricted
/// to this set by an EF check constraint in AppDbContext.
/// </summary>
public static class Fidelity
{
    public const string Measured = "Measured";
    public const string Estimated = "Estimated";
    public const string Placeholder = "Placeholder";
    public const string Manual = "Manual";

    public static readonly IReadOnlyList<string> All =
    [
        Measured, Estimated, Placeholder, Manual
    ];

    /// <summary>
    /// Returns true if the fidelity is one of the allowed values.
    /// Fidelity is a non-nullable column, so null is treated as invalid.
    /// </summary>
    public static bool IsValid(string? fidelity) =>
        fidelity is not null && All.Contains(fidelity);
}
