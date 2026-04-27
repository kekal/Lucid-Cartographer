using System.Text.Json;
using LucidCartographer.Services;

namespace LucidCartographer.Services.Import;

public class GeoJsonImporter(ILogger<GeoJsonImporter> logger) : IFileImporter
{
    public string FormatName => "GeoJSON";

    private static readonly string[] Extensions = [".geojson", ".json"];
    public IReadOnlyList<string> SupportedExtensions => Extensions;

    public async Task<List<ImportedPoi>> ParseAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Parsing GeoJSON file: {FileName}", fileName);
        using var doc = await JsonDocument.ParseAsync(fileStream, cancellationToken: cancellationToken);
        var results = new List<ImportedPoi>();
        var root = doc.RootElement;

        // Determine root type
        var rootType = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

        switch (rootType)
        {
            case "FeatureCollection"
                when root.TryGetProperty("features", out var features)
                     && features.ValueKind == JsonValueKind.Array:
            {
                foreach (var feature in features.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var poi = ParseFeature(feature);
                    if (poi != null)
                    {
                        results.Add(poi);
                    }
                }

                break;
            }
            case "Feature":
            {
                // Standalone Feature
                var poi = ParseFeature(root);
                if (poi != null)
                {
                    results.Add(poi);
                }

                break;
            }
        }

        logger.LogInformation("GeoJSON parse complete: {FileName} — {Count} POIs parsed", fileName, results.Count);
        return results;
    }

    private static ImportedPoi? ParseFeature(JsonElement feature)
    {
        if (!feature.TryGetProperty("geometry", out var geometry))
        {
            return null;
        }

        if (!geometry.TryGetProperty("coordinates", out var coords))
        {
            return null;
        }

        if (coords.ValueKind != JsonValueKind.Array || coords.GetArrayLength() < 2)
        {
            return null;
        }

        // IE-16: Only handle Point geometry. Also reject features with missing geometry type
        // (malformed geometry should not slip through).
        if (!geometry.TryGetProperty("type", out var geoType))
        {
            return null; // Malformed: no geometry type
        }

        if (geoType.GetString() != "Point")
        {
            return null; // Not a Point geometry (LineString, Polygon, etc.)
        }

        if (coords[0].ValueKind != JsonValueKind.Number || coords[1].ValueKind != JsonValueKind.Number)
        {
            return null;
        }

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

        // IE-20 (parity with GpxImporter): only assign to GoogleMapsUrl if
        // the URL is actually a Google Maps link. A generic `url` property
        // (the venue's own website) used to leak into GoogleMapsUrl and
        // poisoned enrichment — the BG service navigated there instead of
        // Google Maps and found no selectors. Generic non-Maps URLs are
        // promoted to Website instead.
        var rawUrl = GetStringProp(props, "google_maps_url")
                     ?? GetStringProp(props, "Google Maps URL")
                     ?? GetStringProp(props, "url")
                     ?? GetStringProp(props, "URL");
        string? googleUrl = null;
        string? website = GetStringProp(props, "website") ?? GetStringProp(props, "Website");
        if (!string.IsNullOrWhiteSpace(rawUrl))
        {
            if (PoiUrlHelper.IsGoogleMapsUrl(rawUrl))
            {
                googleUrl = rawUrl;
            }
            else if (string.IsNullOrWhiteSpace(website))
            {
                website = rawUrl;
            }
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
            Description: description,
            Website: website
        );
    }

    private static string? GetStringProp(JsonElement props, string key)
    {
        if (props.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!props.TryGetProperty(key, out var val))
        {
            return null;
        }

        return val.ValueKind switch
        {
            JsonValueKind.String => val.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => val.ToString()
        };
    }
}
