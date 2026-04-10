using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LucidCartographer.Services.Import;

public class KmlImporter : IFileImporter
{
    public string FormatName => "KML";
    public string[] SupportedExtensions => new[] { ".kml", ".kmz" };

    public async Task<List<ImportedPoi>> ParseAsync(Stream fileStream, string fileName)
    {
        Stream xmlStream = fileStream;

        // Handle KMZ (ZIP containing KML)
        if (Path.GetExtension(fileName).Equals(".kmz", StringComparison.OrdinalIgnoreCase))
        {
            var zip = new System.IO.Compression.ZipArchive(fileStream, System.IO.Compression.ZipArchiveMode.Read);
            var kmlEntry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".kml", StringComparison.OrdinalIgnoreCase));
            if (kmlEntry == null) return new List<ImportedPoi>();
            xmlStream = kmlEntry.Open();
        }

        var doc = await XDocument.LoadAsync(xmlStream, LoadOptions.None, CancellationToken.None);
        var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

        var results = new List<ImportedPoi>();
        var placemarks = doc.Descendants(ns + "Placemark").ToList();
        if (!placemarks.Any())
            placemarks = doc.Descendants("Placemark").ToList();

        foreach (var pm in placemarks)
        {
            var name = pm.Element(ns + "name")?.Value ?? pm.Element("name")?.Value ?? "Unknown";
            var desc = pm.Element(ns + "description")?.Value ?? pm.Element("description")?.Value;

            // Parse coordinates (KML format: lon,lat,alt)
            var coordsText = pm.Descendants(ns + "coordinates").FirstOrDefault()?.Value
                          ?? pm.Descendants("coordinates").FirstOrDefault()?.Value;
            if (coordsText == null) continue;

            var parts = coordsText.Trim().Split(',');
            if (parts.Length < 2) continue;

            if (!double.TryParse(parts[0].Trim(), System.Globalization.CultureInfo.InvariantCulture, out var lon)) continue;
            if (!double.TryParse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture, out var lat)) continue;

            // Try to extract Google Maps URL from description HTML
            string? googleUrl = ExtractGoogleMapsUrl(desc);

            results.Add(new ImportedPoi(
                Name: name.Trim(),
                Latitude: lat,
                Longitude: lon,
                GoogleMapsUrl: googleUrl,
                Description: StripHtml(desc)
            ));
        }
        return results;
    }

    private static string? ExtractGoogleMapsUrl(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        // Simple regex-free extraction: find google.com/maps or maps.google.com URLs
        var idx = html.IndexOf("google.com/maps", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = html.IndexOf("maps.google.com", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        // Walk backward to find URL start (https:// or http://)
        var start = html.LastIndexOf("http", idx, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;

        // Walk forward to find URL end
        var end = idx;
        while (end < html.Length && html[end] != '"' && html[end] != '\'' && html[end] != '<' && html[end] != ' ' && html[end] != '\n')
            end++;

        return html[start..end];
    }

    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        // Simple HTML tag removal
        return Regex.Replace(html, "<[^>]+>", " ").Trim();
    }
}
