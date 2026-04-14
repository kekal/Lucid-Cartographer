namespace LucidCartographer.Data.Entities
{
    /// <summary>
    /// String constants for collection source type values.
    /// [REVIEW-16] Added IsValid method for parity with PoiStatus.
    /// </summary>
    public static class CollectionSourceType
    {
        public const string GpxImport = "gpx_import";
        public const string KmlImport = "kml_import";
        public const string GeoJsonImport = "geojson_import";
        public const string CsvImport = "csv_import";
        public const string GoogleMapsScrape = "google_maps_scrape";
        public const string Manual = "manual";
        public const string OperationResult = "operation_result";

        public static readonly IReadOnlyList<string> All = new[]
        {
            GpxImport, KmlImport, GeoJsonImport, CsvImport, GoogleMapsScrape, Manual, OperationResult
        };

        /// <summary>
        /// Returns true if the source type is valid.
        /// null is treated as valid because SourceType is an optional field on PoiCollection.
        /// </summary>
        public static bool IsValid(string? sourceType) =>
            sourceType is null || All.Contains(sourceType);
    }
}
