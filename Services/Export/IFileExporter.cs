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
        Task ExportAsync(List<Poi> pois, Stream output, string documentName = "Lucid Cartographer Export");

        /// <summary>
        /// Convenience method: exports to a byte array (for backward compatibility).
        /// Prefer <see cref="ExportAsync"/> for large exports.
        /// </summary>
        byte[] Export(List<Poi> pois, string documentName = "Lucid Cartographer Export");
    }
}
