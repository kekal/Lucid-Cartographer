using LucidCartographer.Data.Entities;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace LucidCartographer.Services.Export;

public class GpxExporter : IFileExporter
{
    private static readonly XNamespace Gpx = "http://www.topografix.com/GPX/1/1";

    public string FormatName => "GPX";
    public string ContentType => "application/gpx+xml";

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

        await using var writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            Async = true,
            Encoding = Encoding.UTF8,
            Indent = true
        });
        await doc.WriteToAsync(writer, cancellationToken);
        await writer.FlushAsync();
    }

    private static XDocument BuildDocument(IReadOnlyList<Poi> pois, string documentName)
    {
        return new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(Gpx + "gpx",
                new XAttribute("version", "1.1"),
                new XAttribute("creator", "Lucid Cartographer"),
                new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
                new XElement(Gpx + "metadata",
                    new XElement(Gpx + "name", documentName),
                    new XElement(Gpx + "time", DateTime.UtcNow.ToString("O"))
                ),
                pois.Where(poi => poi is { Latitude: not null, Longitude: not null }).Select(poi =>
                    new XElement(Gpx + "wpt",
                        new XAttribute("lat", poi.Latitude!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        new XAttribute("lon", poi.Longitude!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
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
    }
}
