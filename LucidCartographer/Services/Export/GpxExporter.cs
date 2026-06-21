using System.Collections.Immutable;
using System.Text;
using LucidCartographer.Data.Entities;
using NetTopologySuite.IO;

namespace LucidCartographer.Services.Export;

/// <summary>
/// GPX exporter that skips POIs without coordinates (GPX requires lat/lon on every waypoint).
/// </summary>
public class GpxExporter : IFileExporter
{
    public string FormatName => "GPX";
    public string ContentType => "application/gpx+xml";

    public byte[] Export(IReadOnlyList<Poi> pois, string documentName = "Lucid Cartographer Export")
    {
        var gpx = BuildGpxFile(pois, documentName);
        var xml = gpx.BuildString(new GpxWriterSettings());
        return Encoding.UTF8.GetBytes(xml);
    }

    public async Task ExportAsync(IReadOnlyList<Poi> pois, Stream output, string documentName = "Lucid Cartographer Export", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = Export(pois, documentName);
        await output.WriteAsync(bytes, cancellationToken);
    }

    private static GpxFile BuildGpxFile(IReadOnlyList<Poi> pois, string documentName)
    {
        var gpx = new GpxFile
        {
            Metadata = new GpxMetadata("Lucid Cartographer")
                .WithName(documentName)
                .WithCreationTimeUtc(DateTime.UtcNow)
        };

        foreach (var poi in pois.Where(p => p is { Latitude: not null, Longitude: not null }))
        {
            var wpt = new GpxWaypoint(
                    longitude: new GpxLongitude(poi.Longitude!.Value),
                    latitude: new GpxLatitude(poi.Latitude!.Value))
                .WithName(poi.Name);

            if (!string.IsNullOrEmpty(poi.Notes))
            {
                wpt = wpt.WithDescription(poi.Notes);
            }

            if (!string.IsNullOrEmpty(poi.GoogleMapsUrl)
                && Uri.TryCreate(poi.GoogleMapsUrl, UriKind.Absolute, out var href))
            {
                wpt = wpt.WithLinks(ImmutableArray.Create(new GpxWebLink(href, "Google Maps", null)));
            }

            gpx.Waypoints.Add(wpt);
        }

        return gpx;
    }
}
