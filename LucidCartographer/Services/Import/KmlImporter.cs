using SharpKml.Dom;
using SharpKml.Engine;

namespace LucidCartographer.Services.Import;

/// <summary>
/// KML / KMZ importer backed by SharpKml. Strict spec parser: malformed
/// or non-compliant input surfaces as an exception from the library
/// (typically <see cref="System.Xml.XmlException"/> or
/// <see cref="InvalidOperationException"/>) — no silent placeholder names.
/// </summary>
public class KmlImporter(ILogger<KmlImporter> logger) : IFileImporter
{
    public string FormatName => "KML";

    private static readonly string[] Extensions = [".kml", ".kmz"];
    public IReadOnlyList<string> SupportedExtensions => Extensions;

    public Task<List<ImportedPoi>> ParseAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Parsing KML/KMZ file: {FileName}", fileName);

        KmlFile kml;
        if (Path.GetExtension(fileName).Equals(".kmz", StringComparison.OrdinalIgnoreCase))
        {
            using var kmz = KmzFile.Open(fileStream);
            using var inner = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(kmz.ReadKml()));
            kml = KmlFile.Load(inner);
        }
        else
        {
            kml = KmlFile.Load(fileStream);
        }

        if (kml.Root is null)
        {
            throw new InvalidDataException("KML file is empty or its root element could not be parsed.");
        }

        var results = new List<ImportedPoi>();
        foreach (var placemark in kml.Root.Flatten().OfType<Placemark>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var coords = ExtractCoordinates(placemark.Geometry);
            if (coords is null)
            {
                continue;
            }

            var (lat, lon) = coords.Value;
            var name = placemark.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidDataException(
                    $"Placemark at ({lat:F4}, {lon:F4}) has no <name> element. " +
                    "Re-export the file from a spec-compliant tool.");
            }

            results.Add(new ImportedPoi(
                Name: name,
                Latitude: lat,
                Longitude: lon,
                GoogleMapsUrl: ExtractGoogleMapsUrl(placemark.Description?.Text),
                Address: placemark.Address?.Trim(),
                Description: StripHtml(placemark.Description?.Text),
                FolderName: FindAncestorFolderName(placemark)
            ));
        }

        logger.LogInformation("KML parse complete: {FileName} — {Count} POIs parsed", fileName, results.Count);
        return Task.FromResult(results);
    }

    private static (double lat, double lon)? ExtractCoordinates(Geometry? geometry)
    {
        // Only Point placemarks are POIs in our model. LineStrings,
        // Polygons, and MultiGeometry are skipped silently — same as the
        // previous parser.
        if (geometry is Point point && point.Coordinate is not null)
        {
            return (point.Coordinate.Latitude, point.Coordinate.Longitude);
        }
        return null;
    }

    private static string? FindAncestorFolderName(Feature feature)
    {
        for (var parent = feature.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is Folder folder && !string.IsNullOrWhiteSpace(folder.Name))
            {
                return folder.Name.Trim();
            }
        }
        return null;
    }

    private static string? ExtractGoogleMapsUrl(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return null;
        }

        var idx = html.IndexOf("google.com/maps", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            idx = html.IndexOf("maps.google.com", StringComparison.OrdinalIgnoreCase);
        }
        if (idx < 0)
        {
            return null;
        }

        var start = html.LastIndexOf("http", idx, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        var end = idx;
        while (end < html.Length && html[end] != '"' && html[end] != '\'' && html[end] != '<' && html[end] != ' ' && html[end] != '\n')
        {
            end++;
        }

        return html[start..end];
    }

    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return null;
        }

        var result = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", " ");
        return System.Net.WebUtility.HtmlDecode(result).Trim();
    }
}
