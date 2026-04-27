using LucidCartographer.Services;
using NetTopologySuite.IO;

namespace LucidCartographer.Services.Import;

/// <summary>
/// GPX importer backed by NetTopologySuite.IO.GPX. Strict spec parser:
/// malformed XML or schema-incompatible content surfaces the library's
/// own diagnostic via <see cref="System.Xml.XmlException"/>. Only
/// waypoints (<c>&lt;wpt&gt;</c>) become POIs — routes and tracks are
/// not POIs and are skipped, matching the previous behaviour.
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

            // IE-24: coord-based fallback name (consistent with CsvImporter).
            var name = string.IsNullOrWhiteSpace(wpt.Name)
                ? $"Point ({lat:F4}, {lon:F4})"
                : wpt.Name.Trim();

            // IE-20: only assign to GoogleMapsUrl if the link is actually a
            // Google Maps URL. GPX <link> can point anywhere; stuffing
            // arbitrary URLs into GoogleMapsUrl pollutes dedup and misleads
            // the user.
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
