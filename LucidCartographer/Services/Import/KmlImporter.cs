using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LucidCartographer.Services.Import;

public partial class KmlImporter(ILogger<KmlImporter> logger) : IFileImporter
{
    public string FormatName => "KML";

    private static readonly string[] Extensions = [".kml", ".kmz"];
    public IReadOnlyList<string> SupportedExtensions => Extensions;

    public async Task<List<ImportedPoi>> ParseAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Parsing KML/KMZ file: {FileName}", fileName);
        XDocument doc;

        if (Path.GetExtension(fileName).Equals(".kmz", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = new ZipArchive(fileStream, ZipArchiveMode.Read);
            var kmlEntry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".kml", StringComparison.OrdinalIgnoreCase));
            if (kmlEntry == null)
            {
                return [];
            }

            await using var kmlStream = kmlEntry.Open();
            doc = await XDocument.LoadAsync(kmlStream, LoadOptions.None, cancellationToken);
        }
        else
        {
            doc = await XDocument.LoadAsync(fileStream, LoadOptions.None, cancellationToken);
        }

        var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

        var skipped = 0;
        var results = new List<ImportedPoi>();
        var placemarks = doc.Descendants(ns + "Placemark").ToList();
        if (!placemarks.Any())
        {
            placemarks = doc.Descendants("Placemark").ToList();
        }

        foreach (var pm in placemarks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // IE-24: Use coordinate-based fallback name (consistent with CsvImporter)
            var name = XmlParsingHelpers.FindElement(pm, ns, "name")?.Value;
            var desc = XmlParsingHelpers.FindElement(pm, ns, "description")?.Value;

            var coordsText = XmlParsingHelpers.FindDescendant(pm, ns, "coordinates")?.Value;
            if (coordsText == null) { skipped++; continue; }

            var parts = coordsText.Trim().Split(',');
            if (parts.Length < 2) { skipped++; continue; }

            if (!double.TryParse(parts[0].Trim(), System.Globalization.CultureInfo.InvariantCulture, out var lon)) { skipped++; continue; }
            if (!double.TryParse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture, out var lat)) { skipped++; continue; }

            var googleUrl = ExtractGoogleMapsUrl(desc);
            var effectiveName = string.IsNullOrWhiteSpace(name) ? $"Point ({lat:F4}, {lon:F4})" : name.Trim();

            // Climb ancestors to find the nearest <Folder>'s <name>. Used by
            // the orchestrator to split one KML into multiple collections —
            // one per folder — when the file uses Folder grouping.
            var folderName = FindAncestorFolderName(pm, ns);

            results.Add(new ImportedPoi(
                Name: effectiveName,
                Latitude: lat,
                Longitude: lon,
                GoogleMapsUrl: googleUrl,
                Description: StripHtml(desc),
                FolderName: folderName
            ));
        }

        logger.LogInformation("KML parse complete: {FileName} — {Count} POIs parsed, {Skipped} skipped",
            fileName, results.Count, skipped);
        return results;
    }

    private static string? FindAncestorFolderName(XElement placemark, XNamespace ns)
    {
        for (var ancestor = placemark.Parent; ancestor != null; ancestor = ancestor.Parent)
        {
            var isFolder = ancestor.Name == ns + "Folder" || ancestor.Name.LocalName == "Folder";
            if (!isFolder)
            {
                continue;
            }

            var nameEl = XmlParsingHelpers.FindElement(ancestor, ns, "name");
            var folderName = nameEl?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(folderName))
            {
                return folderName;
            }
        }
        return null;
    }

    // IE-04: FindElement/FindDescendant moved to XmlParsingHelpers

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
            end++;

        return html[start..end];
    }

    /// <summary>
    /// Strips HTML tags from a string using a compiled regex (IE-11).
    /// Single pass is sufficient -- a second identical regex pass cannot match anything new.
    /// </summary>
    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return null;
        }

        var result = HtmlTagRegex().Replace(html, " ");
        return System.Net.WebUtility.HtmlDecode(result).Trim();
    }

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex HtmlTagRegex();
}
