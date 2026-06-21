using LucidCartographer.Services;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace LucidCartographer.Services.Import;

/// <summary>
/// GeoJSON importer backed by NetTopologySuite. Strict spec parser:
/// malformed input surfaces the library's own diagnostic via
/// <see cref="System.Text.Json.JsonException"/>. Only Point features
/// produce POIs; other geometry kinds (LineString, Polygon, …) are
/// skipped silently as before.
/// </summary>
public class GeoJsonImporter(ILogger<GeoJsonImporter> logger) : IFileImporter
{
    public string FormatName => "GeoJSON";

    private static readonly string[] Extensions = [".geojson", ".json"];
    public IReadOnlyList<string> SupportedExtensions => Extensions;

    public async Task<List<ImportedPoi>> ParseAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Parsing GeoJSON file: {FileName}", fileName);

        // The NTS reader is sync-only. Buffer the stream to memory so we
        // honour cancellation while reading the network/disk side.
        using var ms = new MemoryStream();
        await fileStream.CopyToAsync(ms, cancellationToken);
        ms.Position = 0;

        using var sr = new StreamReader(ms);
        var json = await sr.ReadToEndAsync(cancellationToken);

        var reader = new GeoJsonReader();
        var results = new List<ImportedPoi>();
        foreach (var feature in ReadFeatures(reader, json))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var poi = MapFeature(feature);
            if (poi is not null)
            {
                results.Add(poi);
            }
        }

        logger.LogInformation("GeoJSON parse complete: {FileName} — {Count} POIs parsed", fileName, results.Count);
        return results;
    }

    private static IEnumerable<IFeature> ReadFeatures(GeoJsonReader reader, string json)
    {
        // Accept both FeatureCollection and a single Feature at the root;
        // NTS throws JsonException for anything else — that's the library's diagnostic.
        if (json.Contains("\"FeatureCollection\""))
        {
            return reader.Read<FeatureCollection>(json);
        }
        return [reader.Read<IFeature>(json)];
    }

    private static ImportedPoi? MapFeature(IFeature feature)
    {
        if (feature.Geometry is not Point point)
        {
            return null;
        }

        var lat = point.Y;
        var lon = point.X;
        var props = feature.Attributes;

        var name = GetString(props, "name", "Name", "title", "Title")
                   ?? $"Point ({lat:F4}, {lon:F4})";

        var address = GetString(props, "address", "Address", "location");

        var rawUrl = GetString(props, "google_maps_url", "Google Maps URL", "url", "URL");
        string? googleUrl = null;
        var website = GetString(props, "website", "Website");
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

        var description = GetString(props, "description", "Description", "comment");
        var category = GetString(props, "category", "Category", "type");

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

    private static string? GetString(IAttributesTable? props, params string[] keys)
    {
        if (props is null)
        {
            return null;
        }
        foreach (var key in keys)
        {
            if (!props.Exists(key))
            {
                continue;
            }
            var value = props[key];
            if (value is null)
            {
                continue;
            }
            var s = value.ToString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                return s;
            }
        }
        return null;
    }
}
