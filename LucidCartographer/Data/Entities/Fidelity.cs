namespace LucidCartographer.Data.Entities;

/// <summary>
/// String constants for Leg travel-time Fidelity values.
/// Persisted as strings (human-readable DB values), restricted by EF check constraint in AppDbContext.
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
    /// Validates that the fidelity is one of the allowed values; null is invalid.
    /// </summary>
    public static bool IsValid(string? fidelity) =>
        fidelity is not null && All.Contains(fidelity);
}
