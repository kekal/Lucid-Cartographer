using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LucidCartographer.Services.Import
{
    public class KmlImporter : IFileImporter
    {
        public string FormatName => "KML";

        private static readonly string[] _extensions = [".kml", ".kmz"];
        public IReadOnlyList<string> SupportedExtensions => _extensions;

        public async Task<List<ImportedPoi>> ParseAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
        {
            XDocument doc;

            if (Path.GetExtension(fileName).Equals(".kmz", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = new ZipArchive(fileStream, ZipArchiveMode.Read);
                var kmlEntry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".kml", StringComparison.OrdinalIgnoreCase));
                if (kmlEntry == null) return new List<ImportedPoi>();

                using var kmlStream = kmlEntry.Open();
                doc = await XDocument.LoadAsync(kmlStream, LoadOptions.None, cancellationToken);
            }
            else
            {
                doc = await XDocument.LoadAsync(fileStream, LoadOptions.None, cancellationToken);
            }

            var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

            var results = new List<ImportedPoi>();
            var placemarks = doc.Descendants(ns + "Placemark").ToList();
            if (!placemarks.Any())
                placemarks = doc.Descendants("Placemark").ToList();

            foreach (var pm in placemarks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var name = FindElement(pm, ns, "name")?.Value ?? "Unknown";
                var desc = FindElement(pm, ns, "description")?.Value;

                var coordsText = FindDescendant(pm, ns, "coordinates")?.Value;
                if (coordsText == null) continue;

                var parts = coordsText.Trim().Split(',');
                if (parts.Length < 2) continue;

                if (!double.TryParse(parts[0].Trim(), System.Globalization.CultureInfo.InvariantCulture, out var lon)) continue;
                if (!double.TryParse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture, out var lat)) continue;

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

        private static XElement? FindElement(XElement parent, XNamespace ns, string localName)
        {
            return parent.Element(ns + localName) ?? parent.Element(localName);
        }

        private static XElement? FindDescendant(XElement parent, XNamespace ns, string localName)
        {
            return parent.Descendants(ns + localName).FirstOrDefault()
                ?? parent.Descendants(localName).FirstOrDefault();
        }

        private static string? ExtractGoogleMapsUrl(string? html)
        {
            if (string.IsNullOrEmpty(html)) return null;
            var idx = html.IndexOf("google.com/maps", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) idx = html.IndexOf("maps.google.com", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            var start = html.LastIndexOf("http", idx, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return null;

            var end = idx;
            while (end < html.Length && html[end] != '"' && html[end] != '\'' && html[end] != '<' && html[end] != ' ' && html[end] != '\n')
                end++;

            return html[start..end];
        }

        private static string? StripHtml(string? html)
        {
            if (string.IsNullOrEmpty(html)) return null;
            // Strip HTML tags -- iterative approach to handle nested/broken tags
            var result = Regex.Replace(html, "<[^>]*>", " ");
            // Second pass to catch any leftovers from broken tags like <scr<script>ipt>
            result = Regex.Replace(result, "<[^>]*>", " ");
            return System.Net.WebUtility.HtmlDecode(result).Trim();
        }
    }
}
