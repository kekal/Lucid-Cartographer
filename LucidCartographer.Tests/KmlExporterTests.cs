using FluentAssertions;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Export;
using System.Xml.Linq;

namespace LucidCartographer.Tests;

public class KmlExporterTests
{
    private static readonly XNamespace Kml = "http://www.opengis.net/kml/2.2";
    private readonly IFileExporter _exporter = new KmlExporter();

    private static XDocument ParseResult(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return XDocument.Load(ms);
    }

    private static List<Poi> CreateSamplePois() =>
    [
        new Poi
        {
            Name = "Castle",
            Latitude = 50.0541,
            Longitude = 19.9354,
            Address = "1 Castle Rd",
            Category = "Tourism",
            GoogleMapsUrl = "https://maps.google.com/castle"
        },
        new Poi
        {
            Name = "Museum",
            Latitude = 51.5074,
            Longitude = -0.1278,
            Address = "10 Museum St",
            Category = "Culture",
            GoogleMapsUrl = "https://maps.google.com/museum"
        },
        new Poi
        {
            Name = "Cafe",
            Latitude = 48.8566,
            Longitude = 2.3522,
            Category = "Food"
        }
    ];

    [Fact]
    public void Export_ProducesValidXmlWithKmlNamespace()
    {
        var pois = CreateSamplePois();

        var result = _exporter.Export(pois);

        var doc = ParseResult(result);
        var root = doc.Root!;
        root.Name.Should().Be(Kml + "kml");
        root.Name.Namespace.Should().Be(Kml);
    }

    [Fact]
    public void Export_IncludesAllPoisAsPlacemarks()
    {
        var pois = CreateSamplePois();

        var result = _exporter.Export(pois);

        var doc = ParseResult(result);
        var placemarks = doc.Descendants(Kml + "Placemark").ToList();
        placemarks.Should().HaveCount(3);
    }

    [Fact]
    public void Export_IncludesCorrectNameAndCoordinates()
    {
        List<Poi> pois = [new Poi { Name = "TestPlace", Latitude = 50.0541, Longitude = 19.9354 }];

        var result = _exporter.Export(pois);

        var doc = ParseResult(result);
        var placemark = doc.Descendants(Kml + "Placemark").Single();
        placemark.Element(Kml + "name")!.Value.Should().Be("TestPlace");

        // SharpKml serializes doubles at full IEEE round-trip precision
        // (e.g. "19.935400000000001"); compare by parsed value rather than
        // exact string formatting.
        var coords = placemark.Descendants(Kml + "coordinates").Single().Value;
        var parts = coords.Split(',');
        parts.Should().HaveCountGreaterOrEqualTo(2);
        double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture).Should().BeApproximately(19.9354, 1e-6);
        double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture).Should().BeApproximately(50.0541, 1e-6);
    }

    [Fact]
    public void Export_IncludesDescriptionWithAddressCategoryAndGoogleMapsUrl()
    {
        List<Poi> pois =
        [
            new Poi
            {
                Name = "Castle",
                Latitude = 50.0,
                Longitude = 19.0,
                Address = "1 Castle Rd",
                Category = "Tourism",
                GoogleMapsUrl = "https://maps.google.com/castle"
            }
        ];

        var result = _exporter.Export(pois);

        var doc = ParseResult(result);
        var desc = doc.Descendants(Kml + "description").Single().Value;
        desc.Should().Contain("1 Castle Rd");
        desc.Should().Contain("Tourism");
        desc.Should().Contain("https://maps.google.com/castle");
    }

    [Fact]
    public void ExportGroupedByCategory_CreatesFoldersPerCategory()
    {
        var pois = CreateSamplePois();

        var result = _exporter.ExportGroupedByCategory(pois);

        var doc = ParseResult(result);
        var folders = doc.Descendants(Kml + "Folder").ToList();
        folders.Should().HaveCount(3);

        var folderNames = folders.Select(f => f.Element(Kml + "name")!.Value).ToList();
        folderNames.Should().Contain("Tourism");
        folderNames.Should().Contain("Culture");
        folderNames.Should().Contain("Food");
    }

    [Fact]
    public void ExportGroupedByCategory_GroupsPoisCorrectly()
    {
        List<Poi> pois =
        [
            new Poi { Name = "Cafe A", Latitude = 1, Longitude = 1, Category = "Food" },
            new Poi { Name = "Cafe B", Latitude = 2, Longitude = 2, Category = "Food" },
            new Poi { Name = "Museum", Latitude = 3, Longitude = 3, Category = "Culture" }
        ];

        var result = _exporter.ExportGroupedByCategory(pois);

        var doc = ParseResult(result);
        var folders = doc.Descendants(Kml + "Folder").ToList();

        var foodFolder = folders.Single(f => f.Element(Kml + "name")!.Value == "Food");
        foodFolder.Elements(Kml + "Placemark").Should().HaveCount(2);

        var cultureFolder = folders.Single(f => f.Element(Kml + "name")!.Value == "Culture");
        cultureFolder.Elements(Kml + "Placemark").Should().HaveCount(1);
    }

    [Fact]
    public void Export_HandlesPoisWithoutOptionalFields()
    {
        List<Poi> pois = [new Poi { Name = "Minimal", Latitude = 10.0, Longitude = 20.0 }];

        var result = _exporter.Export(pois);

        var doc = ParseResult(result);
        var placemark = doc.Descendants(Kml + "Placemark").Single();
        placemark.Element(Kml + "name")!.Value.Should().Be("Minimal");

        var desc = placemark.Element(Kml + "description")!.Value;
        desc.Should().NotContain("Address");
        desc.Should().NotContain("Category");
        desc.Should().NotContain("Google Maps");
    }

    [Fact]
    public void ExportGroupedByCategory_UsesUncategorizedForNullCategory()
    {
        List<Poi> pois = [new Poi { Name = "NoCategory", Latitude = 1, Longitude = 1, Category = null }];

        var result = _exporter.ExportGroupedByCategory(pois);

        var doc = ParseResult(result);
        var folder = doc.Descendants(Kml + "Folder").Single();
        folder.Element(Kml + "name")!.Value.Should().Be("Uncategorized");
    }
}