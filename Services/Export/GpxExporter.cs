using LucidCartographer.Data.Entities;
using System.Xml.Linq;

namespace LucidCartographer.Services.Export
{
    public class GpxExporter : IFileExporter
    {
        private static readonly XNamespace Gpx = "http://www.topografix.com/GPX/1/1";

        public string FormatName => "GPX";
        public string FileExtension => ".gpx";
        public string ContentType => "application/gpx+xml";

        /// <summary>
        /// Sync wrapper — safe because <see cref="ExportAsync"/> is synchronous
        /// (XDocument.Save is sync and returns Task.CompletedTask).
        /// If ExportAsync ever becomes truly async, this must be revisited.
        /// </summary>
        public byte[] Export(List<Poi> pois, string name = "Lucid Cartographer Export")
        {
            using var ms = new MemoryStream();
            ExportAsync(pois, ms, name).GetAwaiter().GetResult();
            return ms.ToArray();
        }

        public Task ExportAsync(List<Poi> pois, Stream output, string name = "Lucid Cartographer Export")
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

            doc.Save(output);
            return Task.CompletedTask;
        }
    }
}
