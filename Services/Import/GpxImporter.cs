using Microsoft.Extensions.Logging;
using System.Xml.Linq;

namespace LucidCartographer.Services.Import
{
    public class GpxImporter : IFileImporter
    {
        private readonly ILogger<GpxImporter> _logger;

        public GpxImporter(ILogger<GpxImporter> logger)
        {
            _logger = logger;
        }

        public string FormatName => "GPX";

        private static readonly string[] _extensions = [".gpx"];
        public IReadOnlyList<string> SupportedExtensions => _extensions;

        public async Task<List<ImportedPoi>> ParseAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Parsing GPX file: {FileName}", fileName);

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

                var name = FindElement(wpt, ns, "name")?.Value ?? "Unknown";
                var desc = FindElement(wpt, ns, "desc")?.Value;
                var linkHref = FindElement(wpt, ns, "link")?.Attribute("href")?.Value;

                results.Add(new ImportedPoi(
                    Name: name.Trim(),
                    Latitude: lat,
                    Longitude: lon,
                    GoogleMapsUrl: linkHref,
                    Description: desc
                ));
            }

            _logger.LogInformation("GPX parse complete: {FileName} — {Count} POIs parsed, {Skipped} skipped",
                fileName, results.Count, skipped);
            return results;
        }

        /// <summary>
        /// Tries a namespaced lookup first, then falls back to local name only.
        /// </summary>
        private static XElement? FindElement(XElement parent, XNamespace ns, string localName)
        {
            return parent.Element(ns + localName) ?? parent.Element(localName);
        }
    }
}
