using System.Globalization;
using System.Text;
using LucidCartographer.Data.Entities;
using SharpKml.Base;
using SharpKml.Dom;
using SharpKml.Engine;

namespace LucidCartographer.Services.Export;

/// <summary>
/// KML exporter backed by SharpKml. Builds the typed DOM and lets the
/// library handle XML serialization, namespacing, and escaping. Skips
/// POIs with missing coordinates — KML <c>&lt;Point&gt;</c> requires them.
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

    private static string BuildDescription(Poi poi)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(poi.ImageUrl))
        {
            sb.Append(CultureInfo.InvariantCulture, $"<img src=\"{Escape(poi.ImageUrl)}\" style=\"max-width:300px;margin-bottom:8px\" /><br/>");
        }
        if (!string.IsNullOrEmpty(poi.Address))
        {
            sb.Append(CultureInfo.InvariantCulture, $"<b>Address:</b> {Escape(poi.Address)}<br/>");
        }
        if (!string.IsNullOrEmpty(poi.Category))
        {
            sb.Append(CultureInfo.InvariantCulture, $"<b>Category:</b> {Escape(poi.Category)}<br/>");
        }
        if (!string.IsNullOrEmpty(poi.Status))
        {
            sb.Append(CultureInfo.InvariantCulture, $"<b>Status:</b> {Escape(poi.Status)}<br/>");
        }
        if (!string.IsNullOrEmpty(poi.Country))
        {
            sb.Append(CultureInfo.InvariantCulture, $"<b>Country:</b> {Escape(poi.Country)}<br/>");
        }
        if (!string.IsNullOrEmpty(poi.Region))
        {
            sb.Append(CultureInfo.InvariantCulture, $"<b>Region:</b> {Escape(poi.Region)}<br/>");
        }
        if (poi.Rating.HasValue)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<b>My Rating:</b> {poi.Rating}/5<br/>");
        }
        if (poi.GoogleRating.HasValue)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<b>Google Rating:</b> {poi.GoogleRating:F1}");
        }
        if (poi.ReviewCount.HasValue)
        {
            sb.Append(CultureInfo.InvariantCulture, $" ({poi.ReviewCount:N0} reviews)");
        }
        if (poi.GoogleRating.HasValue)
        {
            sb.Append("<br/>");
        }
        if (!string.IsNullOrEmpty(poi.Phone))
        {
            sb.Append(CultureInfo.InvariantCulture, $"<b>Phone:</b> {Escape(poi.Phone)}<br/>");
        }
        if (!string.IsNullOrEmpty(poi.Website))
        {
            sb.Append(CultureInfo.InvariantCulture, $"<b>Website:</b> <a href=\"{Escape(poi.Website)}\">{Escape(poi.Website)}</a><br/>");
        }
        if (!string.IsNullOrEmpty(poi.Notes))
        {
            sb.Append(CultureInfo.InvariantCulture, $"<b>Notes:</b> {Escape(poi.Notes)}<br/>");
        }
        if (poi.VisitedDate.HasValue)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<b>Visited:</b> {poi.VisitedDate.Value:MMM dd, yyyy}<br/>");
        }
        if (!string.IsNullOrEmpty(poi.GoogleMapsUrl))
        {
            sb.Append(CultureInfo.InvariantCulture, $"<a href=\"{Escape(poi.GoogleMapsUrl)}\">Open in Google Maps</a><br/>");
        }

        return sb.ToString();
    }

    private static string Escape(string value)
        => System.Security.SecurityElement.Escape(value);
}
