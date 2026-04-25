using FluentAssertions;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Export;
using System.Xml.Linq;

namespace LucidCartographer.Tests;

public class GpxExporterTests
{
    private static readonly XNamespace Gpx = "http://www.topografix.com/GPX/1/1";
    private readonly GpxExporter _exporter = new();

    private static XDocument ParseResult(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return XDocument.Load(ms);
    }

    [Fact]
    public void Export_ProducesValidXmlWithGpxNamespace()
    {
        List<Poi> pois = [new Poi { Name = "Place", Latitude = 50.0, Longitude = 19.0 }];

        var result = _exporter.Export(pois);

        var doc = ParseResult(result);
        var root = doc.Root!;
        root.Name.Should().Be(Gpx + "gpx");
        root.Attribute("version")!.Value.Should().Be("1.1");
        root.Attribute("creator")!.Value.Should().Be("Lucid Cartographer");
    }

    [Fact]
    public void Export_IncludesAllPoisAsWptElements()
    {
        List<Poi> pois =
        [
            new Poi { Name = "A", Latitude = 1, Longitude = 1 },
            new Poi { Name = "B", Latitude = 2, Longitude = 2 },
            new Poi { Name = "C", Latitude = 3, Longitude = 3 }
        ];

        var result = _exporter.Export(pois);

        var doc = ParseResult(result);
        var wpts = doc.Descendants(Gpx + "wpt").ToList();
        wpts.Should().HaveCount(3);
    }

    [Fact]
    public void Export_IncludesCorrectLatLonAttributes()
    {
        List<Poi> pois = [new Poi { Name = "Place", Latitude = 50.0541, Longitude = 19.9354 }];

        var result = _exporter.Export(pois);

        var doc = ParseResult(result);
        var wpt = doc.Descendants(Gpx + "wpt").Single();
        wpt.Attribute("lat")!.Value.Should().Be("50.0541");
        wpt.Attribute("lon")!.Value.Should().Be("19.9354");
    }

    [Fact]
    public void Export_IncludesNameElement()
    {
        List<Poi> pois = [new Poi { Name = "My Favorite Cafe", Latitude = 1, Longitude = 1 }];

        var result = _exporter.Export(pois);

        var doc = ParseResult(result);
        var wpt = doc.Descendants(Gpx + "wpt").Single();
        wpt.Element(Gpx + "name")!.Value.Should().Be("My Favorite Cafe");
    }

    [Fact]
    public void Export_IncludesLinkElementWithGoogleMapsUrl()
    {
        List<Poi> pois =
        [
            new Poi
            {
                Name = "Place",
                Latitude = 1,
                Longitude = 1,
                GoogleMapsUrl = "https://maps.google.com/place123"
            }
        ];

        var result = _exporter.Export(pois);

        var doc = ParseResult(result);
        var wpt = doc.Descendants(Gpx + "wpt").Single();
        var link = wpt.Element(Gpx + "link");
        link.Should().NotBeNull();
        link!.Attribute("href")!.Value.Should().Be("https://maps.google.com/place123");
        link.Element(Gpx + "text")!.Value.Should().Be("Google Maps");
    }

    [Fact]
    public void Export_HandlesPoisWithoutNotesOrGoogleMapsUrl()
    {
        List<Poi> pois = [new Poi { Name = "Minimal", Latitude = 10.0, Longitude = 20.0 }];

        var result = _exporter.Export(pois);

        var doc = ParseResult(result);
        var wpt = doc.Descendants(Gpx + "wpt").Single();
        wpt.Element(Gpx + "name")!.Value.Should().Be("Minimal");
        wpt.Element(Gpx + "desc").Should().BeNull();
        wpt.Element(Gpx + "link").Should().BeNull();
    }

    [Fact]
    public void Export_IncludesDescWhenNotesProvided()
    {
        List<Poi> pois = [new Poi { Name = "Place", Latitude = 1, Longitude = 1, Notes = "Great view" }];

        var result = _exporter.Export(pois);

        var doc = ParseResult(result);
        var wpt = doc.Descendants(Gpx + "wpt").Single();
        wpt.Element(Gpx + "desc")!.Value.Should().Be("Great view");
    }
}