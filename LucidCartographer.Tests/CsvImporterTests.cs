using FluentAssertions;
using LucidCartographer.Services.Import;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidCartographer.Tests;

public class CsvImporterTests
{
    private readonly CsvImporter _importer = new(NullLogger<CsvImporter>.Instance);

    private Stream OpenSampleFile() =>
        File.OpenRead(Path.Combine("TestData", "sample.csv"));

    [Fact]
    public async Task ParseAsync_ParsesAllRows()
    {
        await using var stream = OpenSampleFile();
        var result = await _importer.ParseAsync(stream, "sample.csv");

        result.Should().HaveCount(3);
    }

    [Fact]
    public async Task ParseAsync_ExtractsNameLatitudeLongitude()
    {
        await using var stream = OpenSampleFile();
        var result = await _importer.ParseAsync(stream, "sample.csv");

        result[0].Name.Should().Be("Zakopane");
        result[0].Latitude.Should().Be(49.2992);
        result[0].Longitude.Should().Be(19.9496);

        result[1].Name.Should().Be("Gdańsk Old Town");
        result[1].Latitude.Should().Be(54.3520);
        result[1].Longitude.Should().Be(18.6466);

        result[2].Name.Should().Be("Malbork Castle");
        result[2].Latitude.Should().Be(54.0401);
        result[2].Longitude.Should().Be(19.0281);
    }

    [Fact]
    public async Task ParseAsync_ExtractsUrlColumn()
    {
        await using var stream = OpenSampleFile();
        var result = await _importer.ParseAsync(stream, "sample.csv");

        result[0].GoogleMapsUrl.Should().Be("https://maps.google.com/place/Zakopane");
        result[2].GoogleMapsUrl.Should().Be("https://maps.google.com/place/Malbork");
    }

    [Fact]
    public async Task ParseAsync_ExtractsCategoryAndDescription()
    {
        await using var stream = OpenSampleFile();
        var result = await _importer.ParseAsync(stream, "sample.csv");

        result[0].Category.Should().Be("Resort Town");
        result[0].Description.Should().Be("Mountain resort in the Tatras");

        result[2].Category.Should().Be("Castle");
        result[2].Description.Should().Be("Largest castle in the world by area");
    }

    [Fact]
    public async Task ParseAsync_HandlesMissingUrl()
    {
        await using var stream = OpenSampleFile();
        var result = await _importer.ParseAsync(stream, "sample.csv");

        // Second row has empty URL
        result[1].GoogleMapsUrl.Should().BeNullOrEmpty();
    }

}