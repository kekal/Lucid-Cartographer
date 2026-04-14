using FluentAssertions;
using LucidCartographer.Services.Import;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidCartographer.Tests
{
    public class GeoJsonImporterTests
    {
        private readonly GeoJsonImporter _importer = new(NullLogger<GeoJsonImporter>.Instance);

        private Stream OpenSampleFile() =>
            File.OpenRead(Path.Combine("TestData", "sample.geojson"));

        [Fact]
        public async Task ParseAsync_ParsesAllFeatures()
        {
            using var stream = OpenSampleFile();
            var result = await _importer.ParseAsync(stream, "sample.geojson");

            result.Should().HaveCount(2);
        }

        [Fact]
        public async Task ParseAsync_ExtractsNameFromProperties()
        {
            using var stream = OpenSampleFile();
            var result = await _importer.ParseAsync(stream, "sample.geojson");

            result[0].Name.Should().Be("Wawel Castle");
            result[1].Name.Should().Be("Old Town Warsaw");
        }

        [Fact]
        public async Task ParseAsync_ExtractsCoordinates_GeoJsonIsLonLat()
        {
            using var stream = OpenSampleFile();
            var result = await _importer.ParseAsync(stream, "sample.geojson");

            // GeoJSON is [lon, lat]
            result[0].Longitude.Should().Be(19.9450);
            result[0].Latitude.Should().Be(50.0647);

            result[1].Longitude.Should().Be(21.0122);
            result[1].Latitude.Should().Be(52.2297);
        }

        [Fact]
        public async Task ParseAsync_ExtractsGoogleMapsUrlFromProperty()
        {
            using var stream = OpenSampleFile();
            var result = await _importer.ParseAsync(stream, "sample.geojson");

            result[0].GoogleMapsUrl.Should().Be("https://www.google.com/maps/place/Wawel");
        }

        [Fact]
        public async Task ParseAsync_ExtractsAddressAndCategory()
        {
            using var stream = OpenSampleFile();
            var result = await _importer.ParseAsync(stream, "sample.geojson");

            result[0].Address.Should().Be("Wawel 5, 31-001 Kraków");
            result[0].Category.Should().Be("Castle");
        }

        [Fact]
        public async Task ParseAsync_HandlesFeatureWithoutOptionalProperties()
        {
            using var stream = OpenSampleFile();
            var result = await _importer.ParseAsync(stream, "sample.geojson");

            var second = result[1];
            second.GoogleMapsUrl.Should().BeNull();
            second.Address.Should().BeNull();
            second.Category.Should().BeNull();
            second.Description.Should().Be("UNESCO World Heritage");
        }
    }
}
