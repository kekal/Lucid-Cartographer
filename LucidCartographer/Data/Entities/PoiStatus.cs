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

        /// <summary>
        /// Returns true if the status is valid.
        /// [REVIEW-17] null is treated as valid because Status is an optional field on Poi.
        /// If a non-null value is provided, it must be one of the known constants.
        /// </summary>
        public static bool IsValid(string? status) =>
            status is null || All.Contains(status);
    }
}
