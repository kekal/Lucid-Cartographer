using FluentAssertions;
using LucidCartographer.Services.Import;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidCartographer.Tests
{
    public class KmlImporterTests
    {
        private readonly KmlImporter _importer = new(NullLogger<KmlImporter>.Instance);

        private Stream OpenSampleFile() =>
            File.OpenRead(Path.Combine("TestData", "sample.kml"));

        [Fact]
        public async Task ParseAsync_ParsesAllPlacemarks()
        {
            using var stream = OpenSampleFile();
            var result = await _importer.ParseAsync(stream, "sample.kml");

            result.Should().HaveCount(2);
        }

        [Fact]
        public async Task ParseAsync_ExtractsNameAndCoordinates()
        {
            using var stream = OpenSampleFile();
            var result = await _importer.ParseAsync(stream, "sample.kml");

            // KML coordinates are lon,lat order
            var first = result[0];
            first.Name.Should().Be("Wieliczka Salt Mine");
            first.Latitude.Should().Be(49.9833);
            first.Longitude.Should().Be(20.0556);

            var second = result[1];
            second.Name.Should().Be("Auschwitz Memorial");
            second.Latitude.Should().Be(50.0344);
            second.Longitude.Should().Be(19.2033);
        }

        [Fact]
        public async Task ParseAsync_ExtractsDescriptionAndStripsHtml()
        {
            using var stream = OpenSampleFile();
            var result = await _importer.ParseAsync(stream, "sample.kml");

            // Second placemark has plain text description
            result[1].Description.Should().Be("Historical museum and memorial");
        }

        [Fact]
        public async Task ParseAsync_ExtractsGoogleMapsUrlFromDescription()
        {
            using var stream = OpenSampleFile();
            var result = await _importer.ParseAsync(stream, "sample.kml");

            // First placemark description contains a Google Maps URL
            result[0].GoogleMapsUrl.Should().Be("https://www.google.com/maps/place/Wieliczka");
        }

        [Fact]
        public async Task ParseAsync_ReturnsNullGoogleMapsUrl_WhenNotInDescription()
        {
            using var stream = OpenSampleFile();
            var result = await _importer.ParseAsync(stream, "sample.kml");

            result[1].GoogleMapsUrl.Should().BeNull();
        }

        [Fact]
        public void SupportedExtensions_ContainsKmlAndKmz()
        {
            _importer.SupportedExtensions.Should().Contain(".kml");
            _importer.SupportedExtensions.Should().Contain(".kmz");
        }
    }
}
