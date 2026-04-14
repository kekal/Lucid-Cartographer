using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services.Export
{
    /// <summary>
    /// Exports a list of POIs to a specific geospatial file format.
    /// </summary>
    public interface IFileExporter
    {
        /// <summary>Human-readable format name, e.g. "KML", "GPX".</summary>
        string FormatName { get; }

        /// <summary>File extension including the leading dot, e.g. ".kml".</summary>
        string FileExtension { get; }

        /// <summary>MIME content type for HTTP responses.</summary>
        string ContentType { get; }

        /// <summary>
        /// Writes the exported content to the given output stream.
        /// </summary>
        Task ExportAsync(IReadOnlyList<Poi> pois, Stream output, string documentName = "Lucid Cartographer Export", CancellationToken cancellationToken = default);

        /// <summary>
        /// Convenience method: exports to a byte array.
        /// </summary>
        byte[] Export(IReadOnlyList<Poi> pois, string documentName = "Lucid Cartographer Export");

        /// <summary>
        /// Whether this exporter supports grouped-by-category export.
        /// </summary>
        bool SupportsGrouping => false;

        /// <summary>
        /// Exports POIs grouped by category. Default implementation falls back to flat export.
        /// </summary>
        byte[] ExportGroupedByCategory(IReadOnlyList<Poi> pois, string documentName = "Lucid Cartographer Export")
            => Export(pois, documentName);
    }
}
