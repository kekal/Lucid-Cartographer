namespace LucidCartographer.Services.Import;

/// <summary>Parses geospatial files (GPX, KML, GeoJSON, CSV, etc.) and returns POIs.</summary>
public interface IFileImporter
{
    /// <summary>Human-readable format name, e.g. "GPX", "KML".</summary>
    string FormatName { get; }

    /// <summary>File extensions this importer handles, including the leading dot (e.g. ".gpx").</summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>Parses the stream and returns imported POIs.</summary>
    Task<List<ImportedPoi>> ParseAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default);
}