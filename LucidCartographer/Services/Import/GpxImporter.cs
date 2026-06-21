using LucidCartographer.Services;
using NetTopologySuite.IO;

namespace LucidCartographer.Services.Import;

/// <summary>
/// GPX importer using NetTopologySuite.IO.GPX. Converts only waypoints to POIs;
/// routes and tracks are skipped. Strict XML validation via <see cref="System.Xml.XmlException"/>.
/// </summary>
public class GpxImporter(ILogger<GpxImporter> logger) : IFileImporter
{
    public string FormatName => "GPX";

    private static readonly string[] Extensions = [".gpx"];
    public IReadOnlyList<string> SupportedExtensions => Extensions;

    public async Task<List<ImportedPoi>> ParseAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Parsing GPX file: {FileName}", fileName);

        // GpxFile.Parse takes a string; buffer the stream so we honour
        // cancellation while reading and let the library handle the rest.
        using var sr = new StreamReader(fileStream);
        var text = await sr.ReadToEndAsync(cancellationToken);

        var gpx = GpxFile.Parse(text, settings: null);

        var results = new List<ImportedPoi>(gpx.Waypoints.Count);
        foreach (var wpt in gpx.Waypoints)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var lat = (double)wpt.Latitude;
            var lon = (double)wpt.Longitude;

            // Fallback to coordinate-based name if no explicit name is provided.
            var name = string.IsNullOrWhiteSpace(wpt.Name)
                ? $"Point ({lat:F4}, {lon:F4})"
                : wpt.Name.Trim();

            // Validate Google Maps URL to prevent dedup pollution from arbitrary links.
            string? googleUrl = null;
            foreach (var link in wpt.Links)
            {
                var href = link.Href?.ToString();
                if (!string.IsNullOrEmpty(href) && PoiUrlHelper.IsGoogleMapsUrl(href))
                {
                    googleUrl = href;
                    break;
                }
            }

            results.Add(new ImportedPoi(
                Name: name,
                Latitude: lat,
                Longitude: lon,
                GoogleMapsUrl: googleUrl,
                Description: wpt.Description ?? wpt.Comment
            ));
        }

        logger.LogInformation("GPX parse complete: {FileName} — {Count} POIs parsed", fileName, results.Count);
        return results;
    }
}
