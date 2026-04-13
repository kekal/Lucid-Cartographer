using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace LucidCartographer.Services.Import
{
    public class GeoJsonImporter : IFileImporter
    {
        private readonly ILogger<GeoJsonImporter> _logger;

        public GeoJsonImporter(ILogger<GeoJsonImporter> logger)
        {
            _logger = logger;
        }

        public string FormatName => "GeoJSON";

        private static readonly string[] _extensions = [".geojson", ".json"];
        public IReadOnlyList<string> SupportedExtensions => _extensions;

        public async Task<List<ImportedPoi>> ParseAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Parsing GeoJSON file: {FileName}", fileName);
            using var doc = await JsonDocument.ParseAsync(fileStream, cancellationToken: cancellationToken);
            var results = new List<ImportedPoi>();
            var root = doc.RootElement;

            // Determine root type
            var rootType = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

            if (rootType == "FeatureCollection"
                && root.TryGetProperty("features", out var features)
                && features.ValueKind == JsonValueKind.Array)
            {
                foreach (var feature in features.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var poi = ParseFeature(feature);
                    if (poi != null) results.Add(poi);
                }
            }
            else if (rootType == "Feature")
            {
                // Standalone Feature
                var poi = ParseFeature(root);
                if (poi != null) results.Add(poi);
            }

            _logger.LogInformation("GeoJSON parse complete: {FileName} — {Count} POIs parsed", fileName, results.Count);
            return results;
        }

        private static ImportedPoi? ParseFeature(JsonElement feature)
        {
            if (!feature.TryGetProperty("geometry", out var geometry)) return null;
            if (!geometry.TryGetProperty("coordinates", out var coords)) return null;
            if (coords.ValueKind != JsonValueKind.Array || coords.GetArrayLength() < 2) return null;

            // IE-16: Only handle Point geometry. Also reject features with missing geometry type
            // (malformed geometry should not slip through).
            if (!geometry.TryGetProperty("type", out var geoType))
                return null; // Malformed: no geometry type
            if (geoType.GetString() != "Point")
                return null; // Not a Point geometry (LineString, Polygon, etc.)

            var lon = coords[0].GetDouble();
            var lat = coords[1].GetDouble();

            var props = feature.TryGetProperty("properties", out var p) ? p : default;

            // IE-24: Use coordinate-based fallback name (consistent with CsvImporter) instead of "Unknown"
            var name = GetStringProp(props, "name")
                    ?? GetStringProp(props, "Name")
                    ?? GetStringProp(props, "title")
                    ?? GetStringProp(props, "Title")
                    ?? $"Point ({lat:F4}, {lon:F4})";

            var address = GetStringProp(props, "address")
                       ?? GetStringProp(props, "Address")
                       ?? GetStringProp(props, "location");

            var googleUrl = GetStringProp(props, "google_maps_url")
                         ?? GetStringProp(props, "url")
                         ?? GetStringProp(props, "URL");

            if (googleUrl == null)
            {
                googleUrl = GetStringProp(props, "Google Maps URL");
            }

            var description = GetStringProp(props, "description")
                           ?? GetStringProp(props, "Description")
                           ?? GetStringProp(props, "comment");

            var category = GetStringProp(props, "category")
                        ?? GetStringProp(props, "Category")
                        ?? GetStringProp(props, "type");

            return new ImportedPoi(
                Name: name.Trim(),
                Latitude: lat,
                Longitude: lon,
                GoogleMapsUrl: googleUrl,
                Address: address,
                Category: category,
                Description: description
            );
        }

        private static string? GetStringProp(JsonElement props, string key)
        {
            if (props.ValueKind != JsonValueKind.Object) return null;
            if (!props.TryGetProperty(key, out var val)) return null;
            if (val.ValueKind == JsonValueKind.String) return val.GetString();
            if (val.ValueKind == JsonValueKind.Null || val.ValueKind == JsonValueKind.Undefined) return null;
            return val.ToString();
        }
    }
}
