using FluentAssertions;
using LucidCartographer.Services.Enrichment;

namespace LucidCartographer.Tests;

public class EnrichedDetailsTests
{
    private static EnrichedDetails Empty() =>
        new(Address: null, Website: null, Phone: null,
            Latitude: null, Longitude: null, GoogleMapsUrl: null, ImageUrl: null);

    [Fact]
    public void ResolvedPlace_False_WhenNothingScraped()
    {
        Empty().ResolvedPlace.Should().BeFalse();
    }

    [Fact]
    public void ResolvedPlace_False_WhenOnlyCoordsPresent()
    {
        // The motivating bug: a name search can land on a viewport (giving
        // @lat,lon) without resolving an actual place. Coords alone — like the
        // ones a POI was created with — must NOT count as a successful pass,
        // otherwise the row is flagged "Enriched" with no photo and no URL.
        var details = Empty() with { Latitude = 54.7099, Longitude = 18.4373 };

        details.ResolvedPlace.Should().BeFalse();
    }

    [Theory]
    [InlineData("Rzucewo 1, 84-100 Rzucewo", null, null, null, null)]
    [InlineData(null, "https://example.com", null, null, null)]
    [InlineData(null, null, "+48 58 000 0000", null, null)]
    [InlineData(null, null, null, "https://lh3.googleusercontent.com/p/x", null)]
    [InlineData(null, null, null, null, "https://www.google.com/maps/place/X/@1,2,17z")]
    public void ResolvedPlace_True_WhenAnyRealFieldScraped(
        string? address, string? website, string? phone, string? imageUrl, string? mapsUrl)
    {
        var details = Empty() with
        {
            Address = address,
            Website = website,
            Phone = phone,
            ImageUrl = imageUrl,
            GoogleMapsUrl = mapsUrl
        };

        details.ResolvedPlace.Should().BeTrue();
    }

    [Fact]
    public void ResolvedPlace_False_WhenFieldsAreWhitespace()
    {
        var details = Empty() with { Address = "   ", Website = "" };

        details.ResolvedPlace.Should().BeFalse();
    }
}
