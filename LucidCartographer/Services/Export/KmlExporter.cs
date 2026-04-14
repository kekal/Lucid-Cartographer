using LucidCartographer.Data.Entities;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace LucidCartographer.Services.Export
{
    public class KmlExporter : IFileExporter
    {
        private static readonly XNamespace Kml = "http://www.opengis.net/kml/2.2";

        public string FormatName => "KML";
        public string FileExtension => ".kml";
        public string ContentType => "application/vnd.google-earth.kml+xml";
        public bool SupportsGrouping => true;

        /// <summary>
        /// Synchronous export — builds the XDocument and writes directly to a MemoryStream.
        /// No async wrapper; no deadlock risk.
        /// </summary>
        public byte[] Export(IReadOnlyList<Poi> pois, string documentName = "Lucid Cartographer Export")
        {
            var doc = BuildDocument(pois, documentName);
            using var ms = new MemoryStream();
            doc.Save(ms);
            return ms.ToArray();
        }

        public async Task ExportAsync(IReadOnlyList<Poi> pois, Stream output, string documentName = "Lucid Cartographer Export", CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var doc = BuildDocument(pois, documentName);

            using var writer = XmlWriter.Create(output, new XmlWriterSettings
            {
                Async = true,
                Encoding = Encoding.UTF8,
                Indent = true
            });
            await doc.WriteToAsync(writer, cancellationToken);
            await writer.FlushAsync();
        }

        public byte[] ExportGroupedByCategory(IReadOnlyList<Poi> pois, string documentName = "Lucid Cartographer Export")
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

        private static XDocument BuildDocument(IReadOnlyList<Poi> pois, string documentName)
        {
            return new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement(Kml + "kml",
                    new XElement(Kml + "Document",
                        new XElement(Kml + "name", documentName),
                        GeneratePlacemarks(pois)
                    )
                )
            );
        }

        private static IEnumerable<XElement> GeneratePlacemarks(IReadOnlyList<Poi> pois)
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
