using System.Xml.Linq;

namespace LucidCartographer.Services.Import;

public class GpxImporter : IFileImporter
{
    public string FormatName => "GPX";
    public string[] SupportedExtensions => new[] { ".gpx" };

    public async Task<List<ImportedPoi>> ParseAsync(Stream fileStream, string fileName)
    {
        var doc = await XDocument.LoadAsync(fileStream, LoadOptions.None, CancellationToken.None);
        var root = doc.Root!;

        // Handle GPX namespace or no namespace
        var ns = root.GetDefaultNamespace();

        var waypoints = root.Descendants(ns + "wpt").ToList();
        if (!waypoints.Any())
        {
            // Try without namespace
            waypoints = root.Descendants("wpt").ToList();
        }

        var results = new List<ImportedPoi>();
        foreach (var wpt in waypoints)
        {
            var latStr = wpt.Attribute("lat")?.Value;
            var lonStr = wpt.Attribute("lon")?.Value;
            if (latStr == null || lonStr == null) continue;

            if (!double.TryParse(latStr, System.Globalization.CultureInfo.InvariantCulture, out var lat)) continue;
            if (!double.TryParse(lonStr, System.Globalization.CultureInfo.InvariantCulture, out var lon)) continue;

            var name = wpt.Element(ns + "name")?.Value ?? wpt.Element("name")?.Value ?? "Unknown";
            var desc = wpt.Element(ns + "desc")?.Value ?? wpt.Element("desc")?.Value;
            var linkHref = wpt.Element(ns + "link")?.Attribute("href")?.Value
                        ?? wpt.Element("link")?.Attribute("href")?.Value;

            results.Add(new ImportedPoi(
                Name: name.Trim(),
                Latitude: lat,
                Longitude: lon,
                GoogleMapsUrl: linkHref,
                Description: desc
            ));
        }
        return results;
    }
}
