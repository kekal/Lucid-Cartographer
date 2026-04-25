using FluentAssertions;
using LucidCartographer.Services.Import;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidCartographer.Tests;

public class GpxImporterTests
{
    private readonly GpxImporter _importer = new(NullLogger<GpxImporter>.Instance);

    private Stream OpenSampleFile() =>
        File.OpenRead(Path.Combine("TestData", "sample.gpx"));

    [Fact]
    public async Task ParseAsync_ParsesAllWaypoints()
    {
        await using var stream = OpenSampleFile();
        var result = await _importer.ParseAsync(stream, "sample.gpx");

        result.Should().HaveCount(3);
    }

    [Fact]
    public async Task ParseAsync_ExtractsCorrectNameLatLon()
    {
        await using var stream = OpenSampleFile();
        var result = await _importer.ParseAsync(stream, "sample.gpx");

        var first = result[0];
        first.Name.Should().Be("Wawel Castle");
        first.Latitude.Should().Be(50.0647);
        first.Longitude.Should().Be(19.9450);

        var second = result[1];
        second.Name.Should().Be("Palace of Culture and Science");
        second.Latitude.Should().Be(52.2297);
        second.Longitude.Should().Be(21.0122);

        var third = result[2];
        third.Name.Should().Be("Wrocław Market Square");
        third.Latitude.Should().Be(51.1079);
        third.Longitude.Should().Be(17.0385);
    }

    [Fact]
    public async Task ParseAsync_ExtractsGoogleMapsLinkHref()
    {
        await using var stream = OpenSampleFile();
        var result = await _importer.ParseAsync(stream, "sample.gpx");

        result[0].GoogleMapsUrl.Should().Be("https://www.google.com/maps/place/Wawel+Castle/@50.0547,19.9352,17z");
        result[1].GoogleMapsUrl.Should().Be("https://www.google.com/maps/place/Palace+of+Culture/@52.2319,21.0067,17z");
    }

    [Fact]
    public async Task ParseAsync_ExtractsDescription()
    {
        await using var stream = OpenSampleFile();
        var result = await _importer.ParseAsync(stream, "sample.gpx");

        result[0].Description.Should().Be("Historic royal castle in Kraków");
        result[1].Description.Should().Be("Tallest building in Warsaw");
    }

    [Fact]
    public async Task ParseAsync_HandlesWaypointWithoutDescriptionOrLink()
    {
        await using var stream = OpenSampleFile();
        var result = await _importer.ParseAsync(stream, "sample.gpx");

        var third = result[2];
        third.Name.Should().Be("Wrocław Market Square");
        third.Description.Should().BeNull();
        third.GoogleMapsUrl.Should().BeNull();
    }

}