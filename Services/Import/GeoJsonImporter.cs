using System.Text.Json;

namespace LucidCartographer.Services.Import
{
    public class GeoJsonImporter : IFileImporter
    {
        public string FormatName => "GeoJSON";
        public string[] SupportedExtensions => new[] { ".geojson", ".json" };

        public async Task<List<ImportedPoi>> ParseAsync(Stream fileStream, string fileName)
        {
            var doc = await JsonDocument.ParseAsync(fileStream);
            var results = new List<ImportedPoi>();
            var root = doc.RootElement;

            // Handle both FeatureCollection and direct Feature array
            JsonElement features;
            if (root.TryGetProperty("features", out features) && features.ValueKind == JsonValueKind.Array)
            {
                foreach (var feature in features.EnumerateArray())
                {
                    var poi = ParseFeature(feature);
                    if (poi != null) results.Add(poi);
                }
            }

            return results;
        }

        private static ImportedPoi? ParseFeature(JsonElement feature)
        {
            // Get geometry
            if (!feature.TryGetProperty("geometry", out var geometry)) return null;
            if (!geometry.TryGetProperty("coordinates", out var coords)) return null;
            if (coords.ValueKind != JsonValueKind.Array || coords.GetArrayLength() < 2) return null;

            var lon = coords[0].GetDouble();
            var lat = coords[1].GetDouble();

            // Get properties
            var props = feature.TryGetProperty("properties", out var p) ? p : default;

            var name = GetStringProp(props, "name")
                    ?? GetStringProp(props, "Name")
                    ?? GetStringProp(props, "title")
                    ?? GetStringProp(props, "Title")
                    ?? "Unknown";

            var address = GetStringProp(props, "address")
                       ?? GetStringProp(props, "Address")
                       ?? GetStringProp(props, "location");

            var googleUrl = GetStringProp(props, "google_maps_url")
                         ?? GetStringProp(props, "url")
                         ?? GetStringProp(props, "URL");

            // Google Takeout specific: look for "Google Maps URL" field
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
