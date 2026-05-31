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
        // A name search can land on a viewport (giving @lat,lon) without
        // resolving an actual place. Coords alone must NOT count as success.
        var details = Empty() with { Latitude = 54.7099, Longitude = 18.4373 };

        details.ResolvedPlace.Should().BeFalse();
    }

    [Theory]
    [InlineData("Rzucewo 1, 84-100 Rzucewo", null, null, null)]
    [InlineData(null, "https://example.com", null, null)]
    [InlineData(null, null, "+48 58 000 0000", null)]
    [InlineData(null, null, null, "https://lh3.googleusercontent.com/p/x")]
    public void ResolvedPlace_False_WhenNoCanonicalPlaceUrl(
        string? address, string? website, string? phone, string? imageUrl)
    {
        // Address / website / phone / photo on their own no longer count:
        // without a canonical /maps/place/ URL we never confirmed the place.
        var details = Empty() with
        {
            Address = address,
            Website = website,
            Phone = phone,
            ImageUrl = imageUrl
        };

        details.ResolvedPlace.Should().BeFalse();
    }

    [Fact]
    public void ResolvedPlace_False_WhenPhotoButNoUrl_Poi604Regression()
    {
        // POI #604 ("Sztolnia Królowa Luiza / Kopalnia Guido"): the name
        // search landed on a SERP, a stray googleusercontent.com thumbnail
        // (the "PUB 320" bar menu) was grabbed, and the row was wrongly
        // flagged "Enriched" with no canonical URL. A photo without a place
        // URL must resolve to FALSE.
        var details = Empty() with
        {
            ImageUrl = "https://lh3.googleusercontent.com/gps-cs-s/STRAY=w1024",
            GoogleMapsUrl = null
        };

        details.ResolvedPlace.Should().BeFalse();
    }

    [Fact]
    public void ResolvedPlace_True_WhenCanonicalPlaceUrlPresent()
    {
        var details = Empty() with
        {
            GoogleMapsUrl = "https://www.google.com/maps/place/X/@1,2,17z"
        };

        details.ResolvedPlace.Should().BeTrue();
    }

    [Fact]
    public void ResolvedPlace_False_WhenGoogleMapsUrlIsWhitespace()
    {
        var details = Empty() with { GoogleMapsUrl = "   " };

        details.ResolvedPlace.Should().BeFalse();
    }
}
