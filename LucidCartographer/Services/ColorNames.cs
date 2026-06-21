namespace LucidCartographer.Services;

/// <summary>
/// Maps the 8 palette hex colors to human-readable names for screen reader accessibility.
/// </summary>
public static class ColorNames
{
    private static readonly Dictionary<string, string> HexToName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["#005bbf"] = "Blue",
        ["#006e2c"] = "Green",
        ["#b81d17"] = "Red",
        ["#7c3aed"] = "Purple",
        ["#ca8a04"] = "Amber",
        ["#0891b2"] = "Cyan",
        ["#be185d"] = "Pink",
        ["#4b5563"] = "Gray",
    };

    /// <summary>
    /// Returns a human-readable color name, or the hex code itself if not in the palette.
    /// </summary>
    public static string GetName(string hexColor) => HexToName.TryGetValue(hexColor, out var name) ? name : hexColor;
}
