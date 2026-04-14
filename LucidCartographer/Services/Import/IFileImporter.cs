namespace LucidCartographer.Services.Import
{
    /// <summary>
    /// Parses a geospatial file (GPX, KML, GeoJSON, CSV, etc.) and returns a flat list of POIs.
    /// </summary>
    public interface IFileImporter
    {
        /// <summary>Human-readable format name, e.g. "GPX", "KML".</summary>
        string FormatName { get; }

        /// <summary>File extensions this importer handles, including the leading dot (e.g. ".gpx").</summary>
        IReadOnlyList<string> SupportedExtensions { get; }

        /// <summary>
        /// Parses the given stream and returns imported POIs.
        /// </summary>
        Task<List<ImportedPoi>> ParseAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default);
    }
}
