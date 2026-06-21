using System.Globalization;
using System.Text;
using System.Xml.Linq;
using LucidCartographer.Data.Entities;
using SharpKml.Base;
using SharpKml.Dom;
using SharpKml.Engine;

namespace LucidCartographer.Services.Export;

/// <summary>
/// KML exporter that skips POIs with missing coordinates (KML <c>&lt;Point&gt;</c> requires them).
/// </summary>
public class KmlExporter : IFileExporter
{
    public string FormatName => "KML";
    public string ContentType => "application/vnd.google-earth.kml+xml";

    public byte[] Export(IReadOnlyList<Poi> pois, string documentName = "Lucid Cartographer Export")
    {
        var kml = BuildFlat(pois, documentName);
        return Serialize(kml);
    }

    public async Task ExportAsync(IReadOnlyList<Poi> pois, Stream output, string documentName = "Lucid Cartographer Export", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = Export(pois, documentName);
        await output.WriteAsync(bytes, cancellationToken);
    }

    public byte[] ExportGroupedByCategory(IReadOnlyList<Poi> pois, string documentName = "Lucid Cartographer Export")
    {
        var doc = new Document { Name = documentName };
        foreach (var group in pois.GroupBy(p => p.Category ?? "Uncategorized"))
        {
            var folder = new Folder { Name = group.Key };
            foreach (var placemark in BuildPlacemarks(group))
            {
                folder.AddFeature(placemark);
            }
            doc.AddFeature(folder);
        }
        return Serialize(new Kml { Feature = doc });
    }

    private static Kml BuildFlat(IReadOnlyList<Poi> pois, string documentName)
    {
        var doc = new Document { Name = documentName };
        foreach (var placemark in BuildPlacemarks(pois))
        {
            doc.AddFeature(placemark);
        }
        return new Kml { Feature = doc };
    }

    private static IEnumerable<Placemark> BuildPlacemarks(IEnumerable<Poi> pois)
    {
        foreach (var poi in pois.Where(p => p is { Latitude: not null, Longitude: not null }))
        {
            yield return new Placemark
            {
                Name = poi.Name,
                Description = new Description { Text = BuildDescription(poi) },
                Geometry = new Point
                {
                    Coordinate = new Vector(poi.Latitude!.Value, poi.Longitude!.Value)
                }
            };
        }
    }

    private static byte[] Serialize(Kml kml)
    {
        var serializer = new Serializer();
        serializer.Serialize(kml);
        return Encoding.UTF8.GetBytes(serializer.Xml);
    }

    /// <summary>
    /// Builds the HTML balloon body for a placemark using <see cref="XElement"/> for automatic escaping.
    /// </summary>
    private static string BuildDescription(Poi poi)
    {
        var nodes = new List<XNode>();

        if (!string.IsNullOrEmpty(poi.ImageUrl))
        {
            nodes.Add(new XElement("img",
                new XAttribute("src", poi.ImageUrl),
                new XAttribute("style", "max-width:300px;margin-bottom:8px")));
            nodes.Add(new XElement("br"));
        }

        AddLabelled(nodes, "Address", poi.Address);
        AddLabelled(nodes, "Category", poi.Category);
        AddLabelled(nodes, "Country", poi.Country);
        AddLabelled(nodes, "Region", poi.Region);

        if (poi.Rating.HasValue)
        {
            AddLabelled(nodes, "My Rating", string.Create(CultureInfo.InvariantCulture, $"{poi.Rating}/5"));
        }

        if (poi.GoogleRating.HasValue)
        {
            var rating = poi.GoogleRating.Value.ToString("F1", CultureInfo.InvariantCulture);
            var review = poi.ReviewCount.HasValue
                ? string.Create(CultureInfo.InvariantCulture, $" ({poi.ReviewCount.Value:N0} reviews)")
                : "";
            nodes.Add(new XElement("b", "Google Rating:"));
            nodes.Add(new XText(" " + rating + review));
            nodes.Add(new XElement("br"));
        }

        AddLabelled(nodes, "Phone", poi.Phone);

        if (!string.IsNullOrEmpty(poi.Website))
        {
            nodes.Add(new XElement("b", "Website:"));
            nodes.Add(new XText(" "));
            nodes.Add(new XElement("a", new XAttribute("href", poi.Website), poi.Website));
            nodes.Add(new XElement("br"));
        }

        AddLabelled(nodes, "Notes", poi.Notes);

        if (!string.IsNullOrEmpty(poi.GoogleMapsUrl))
        {
            nodes.Add(new XElement("a",
                new XAttribute("href", poi.GoogleMapsUrl),
                "Open in Google Maps"));
            nodes.Add(new XElement("br"));
        }

        var sb = new StringBuilder();
        foreach (var node in nodes)
        {
            sb.Append(node.ToString(SaveOptions.DisableFormatting));
        }
        return sb.ToString();
    }

    private static void AddLabelled(List<XNode> nodes, string label, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }
        nodes.Add(new XElement("b", label + ":"));
        nodes.Add(new XText(" " + value));
        nodes.Add(new XElement("br"));
    }
}
