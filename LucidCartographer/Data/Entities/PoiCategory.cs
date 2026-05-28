namespace LucidCartographer.Data.Entities;

/// <summary>
/// String constants for POI category values.
/// </summary>
public static class PoiCategory
{
    public const string Restaurant = "restaurant";
    public const string Cafe = "cafe";
    public const string Bar = "bar";
    public const string Hotel = "hotel";
    public const string Attraction = "attraction";
    public const string Shopping = "shopping";
    public const string Nature = "nature";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All =
    [
        Restaurant, Cafe, Bar, Hotel, Attraction, Shopping, Nature, Other
    ];

    /// <summary>
    /// Returns true if the category is valid.
    /// null is treated as valid because Category is an optional field on Poi.
    /// </summary>
    public static bool IsValid(string? category) =>
        category is null || All.Contains(category);
}
