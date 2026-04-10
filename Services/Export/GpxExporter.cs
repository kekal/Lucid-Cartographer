using System.Xml.Linq;
using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services.Export;

public class GpxExporter
{
    private static readonly XNamespace Gpx = "http://www.topografix.com/GPX/1/1";

    public byte[] Export(List<Poi> pois, string name = "Lucid Cartographer Export")
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(Gpx + "gpx",
                new XAttribute("version", "1.1"),
                new XAttribute("creator", "Lucid Cartographer"),
                new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
                new XElement(Gpx + "metadata",
                    new XElement(Gpx + "name", name),
                    new XElement(Gpx + "time", DateTime.UtcNow.ToString("O"))
                ),
                pois.Select(poi =>
                    new XElement(Gpx + "wpt",
                        new XAttribute("lat", poi.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        new XAttribute("lon", poi.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        new XElement(Gpx + "name", poi.Name),
                        string.IsNullOrEmpty(poi.Notes) ? null : new XElement(Gpx + "desc", poi.Notes),
                        string.IsNullOrEmpty(poi.GoogleMapsUrl) ? null : new XElement(Gpx + "link",
                            new XAttribute("href", poi.GoogleMapsUrl),
                            new XElement(Gpx + "text", "Google Maps")
                        )
                    )
                )
            )
        );

        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }
}
