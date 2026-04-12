namespace LucidCartographer.Data.Entities
{
    /// <summary>
    /// String constants for POI status values. Using constants instead of enum
    /// to avoid DB migration complexity while providing compile-time safety.
    /// </summary>
    public static class PoiStatus
    {
        public const string Visited = "visited";
        public const string WantToGo = "want_to_go";
        public const string Imported = "imported";

        public static readonly IReadOnlyList<string> All = new[] { Visited, WantToGo, Imported };

        public static bool IsValid(string? status) =>
            status is null || All.Contains(status);
    }

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

        public static readonly IReadOnlyList<string> All = new[]
        {
            Restaurant, Cafe, Bar, Hotel, Attraction, Shopping, Nature, Other
        };
    }

    /// <summary>
    /// String constants for collection source type values.
    /// </summary>
    public static class CollectionSourceType
    {
        public const string GpxImport = "gpx_import";
        public const string KmlImport = "kml_import";
        public const string Manual = "manual";
        public const string OperationResult = "operation_result";

        public static readonly IReadOnlyList<string> All = new[]
        {
            GpxImport, KmlImport, Manual, OperationResult
        };
    }
}
