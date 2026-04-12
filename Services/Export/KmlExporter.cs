using LucidCartographer.Data.Entities;
using System.Text;
using System.Xml.Linq;

namespace LucidCartographer.Services.Export
{
    public class KmlExporter
    {
        private static readonly XNamespace Kml = "http://www.opengis.net/kml/2.2";

        public byte[] Export(List<Poi> pois, string documentName = "Lucid Cartographer Export")
        {
            var doc = new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement(Kml + "kml",
                    new XElement(Kml + "Document",
                        new XElement(Kml + "name", documentName),
                        GeneratePlacemarks(pois)
                    )
                )
            );

            using var ms = new MemoryStream();
            doc.Save(ms);
            return ms.ToArray();
        }

        public byte[] ExportGroupedByCategory(List<Poi> pois, string documentName = "Lucid Cartographer Export")
        {
            var grouped = pois.GroupBy(p => p.Category ?? "Uncategorized");

            var doc = new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement(Kml + "kml",
                    new XElement(Kml + "Document",
                        new XElement(Kml + "name", documentName),
                        grouped.Select(g =>
                            new XElement(Kml + "Folder",
                                new XElement(Kml + "name", g.Key),
                                GeneratePlacemarks(g.ToList())
                            )
                        )
                    )
                )
            );

            using var ms = new MemoryStream();
            doc.Save(ms);
            return ms.ToArray();
        }

        private IEnumerable<XElement> GeneratePlacemarks(List<Poi> pois)
        {
            return pois.Select(poi =>
                new XElement(Kml + "Placemark",
                    new XElement(Kml + "name", poi.Name),
                    new XElement(Kml + "description", BuildDescription(poi)),
                    new XElement(Kml + "Point",
                        new XElement(Kml + "coordinates", $"{poi.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)},{poi.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)},0")
                    )
                )
            );
        }

        private static string BuildDescription(Poi poi)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(poi.Address))
                sb.AppendLine($"Address: {poi.Address}");
            if (!string.IsNullOrEmpty(poi.Category))
                sb.AppendLine($"Category: {poi.Category}");
            if (!string.IsNullOrEmpty(poi.Status))
                sb.AppendLine($"Status: {poi.Status}");
            if (!string.IsNullOrEmpty(poi.Notes))
                sb.AppendLine($"Notes: {poi.Notes}");
            if (!string.IsNullOrEmpty(poi.GoogleMapsUrl))
                sb.AppendLine($"Google Maps: {poi.GoogleMapsUrl}");
            return sb.ToString();
        }
    }
}
