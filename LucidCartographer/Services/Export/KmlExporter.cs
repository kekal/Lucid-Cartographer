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
                    new XElement(Kml + "description", new XCData(BuildDescription(poi))),
                    new XElement(Kml + "Point",
                        new XElement(Kml + "coordinates", $"{poi.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)},{poi.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)},0")
                    )
                )
            );
        }

        private static string BuildDescription(Poi poi)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrEmpty(poi.ImageUrl))
                sb.Append($"<img src=\"{Escape(poi.ImageUrl)}\" style=\"max-width:300px;margin-bottom:8px\" /><br/>");

            if (!string.IsNullOrEmpty(poi.Address))
                sb.Append($"<b>Address:</b> {Escape(poi.Address)}<br/>");
            if (!string.IsNullOrEmpty(poi.Category))
                sb.Append($"<b>Category:</b> {Escape(poi.Category)}<br/>");
            if (!string.IsNullOrEmpty(poi.Status))
                sb.Append($"<b>Status:</b> {Escape(poi.Status)}<br/>");
            if (!string.IsNullOrEmpty(poi.Country))
                sb.Append($"<b>Country:</b> {Escape(poi.Country)}<br/>");
            if (!string.IsNullOrEmpty(poi.Region))
                sb.Append($"<b>Region:</b> {Escape(poi.Region)}<br/>");
            if (poi.Rating.HasValue)
                sb.Append($"<b>My Rating:</b> {poi.Rating}/5<br/>");
            if (poi.GoogleRating.HasValue)
                sb.Append($"<b>Google Rating:</b> {poi.GoogleRating:F1}");
            if (poi.ReviewCount.HasValue)
                sb.Append($" ({poi.ReviewCount:N0} reviews)");
            if (poi.GoogleRating.HasValue)
                sb.Append("<br/>");
            if (!string.IsNullOrEmpty(poi.Phone))
                sb.Append($"<b>Phone:</b> {Escape(poi.Phone)}<br/>");
            if (!string.IsNullOrEmpty(poi.Website))
                sb.Append($"<b>Website:</b> <a href=\"{Escape(poi.Website)}\">{Escape(poi.Website)}</a><br/>");
            if (!string.IsNullOrEmpty(poi.Notes))
                sb.Append($"<b>Notes:</b> {Escape(poi.Notes)}<br/>");
            if (poi.VisitedDate.HasValue)
                sb.Append($"<b>Visited:</b> {poi.VisitedDate.Value:MMM dd, yyyy}<br/>");
            if (!string.IsNullOrEmpty(poi.GoogleMapsUrl))
                sb.Append($"<a href=\"{Escape(poi.GoogleMapsUrl)}\">Open in Google Maps</a><br/>");

            return sb.ToString();
        }

        private static string Escape(string value)
            => System.Security.SecurityElement.Escape(value);

    }
}
