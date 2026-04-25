using System.Xml.Linq;

namespace LucidCartographer.Services.Import;

public class GpxImporter(ILogger<GpxImporter> logger) : IFileImporter
{
    public string FormatName => "GPX";

    private static readonly string[] Extensions = [".gpx"];
    public IReadOnlyList<string> SupportedExtensions => Extensions;

    public async Task<List<ImportedPoi>> ParseAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Parsing GPX file: {FileName}", fileName);

        var doc = await XDocument.LoadAsync(fileStream, LoadOptions.None, cancellationToken);
        var root = doc.Root!;

        var ns = root.GetDefaultNamespace();

        var waypoints = root.Descendants(ns + "wpt").ToList();
        if (!waypoints.Any())
        {
            waypoints = root.Descendants("wpt").ToList();
        }

        var skipped = 0;
        var results = new List<ImportedPoi>();
        foreach (var wpt in waypoints)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var latStr = wpt.Attribute("lat")?.Value;
            var lonStr = wpt.Attribute("lon")?.Value;
            if (latStr == null || lonStr == null) { skipped++; continue; }

            if (!double.TryParse(latStr, System.Globalization.CultureInfo.InvariantCulture, out var lat)) { skipped++; continue; }
            if (!double.TryParse(lonStr, System.Globalization.CultureInfo.InvariantCulture, out var lon)) { skipped++; continue; }

            // IE-24: Use coordinate-based fallback name (consistent with CsvImporter)
            var name = XmlParsingHelpers.FindElement(wpt, ns, "name")?.Value ?? $"Point ({lat:F4}, {lon:F4})";
            var desc = XmlParsingHelpers.FindElement(wpt, ns, "desc")?.Value;
            var linkHref = XmlParsingHelpers.FindElement(wpt, ns, "link")?.Attribute("href")?.Value;

            // IE-20: Only assign to GoogleMapsUrl if the link is actually a Google Maps URL.
            // GPX <link> can point to any website; stuffing arbitrary URLs into GoogleMapsUrl
            // pollutes dedup logic and misleads the user.
            string? googleUrl = null;
            if (linkHref != null && (linkHref.Contains("google.com/maps", StringComparison.OrdinalIgnoreCase)
                                     || linkHref.Contains("maps.google.com", StringComparison.OrdinalIgnoreCase)
                                     || linkHref.Contains("maps.app.goo.gl", StringComparison.OrdinalIgnoreCase)))
            {
                googleUrl = linkHref;
            }

            results.Add(new ImportedPoi(
                Name: name.Trim(),
                Latitude: lat,
                Longitude: lon,
                GoogleMapsUrl: googleUrl,
                Description: desc
            ));
        }

        logger.LogInformation("GPX parse complete: {FileName} — {Count} POIs parsed, {Skipped} skipped",
            fileName, results.Count, skipped);
        return results;
    }

    // IE-04: FindElement moved to XmlParsingHelpers
}
