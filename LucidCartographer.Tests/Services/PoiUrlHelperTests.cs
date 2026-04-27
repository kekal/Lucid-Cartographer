using FluentAssertions;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;

namespace LucidCartographer.Tests;

/// <summary>
/// PoiUrlHelper — pure helper, was at ~25% line coverage.
/// </summary>
public class PoiUrlHelperTests
{
    [Theory]
    [InlineData("https://www.google.com/maps/place/Wieliczka", true)]
    [InlineData("https://maps.google.com/?q=foo", true)]
    [InlineData("https://maps.app.goo.gl/abc", true)]
    [InlineData("https://goo.gl/maps/legacy", true)]
    [InlineData("https://goo.gl/random", false)]
    [InlineData("https://example.com/foo", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    public void IsGoogleMapsUrl_RecognizesGoogleHostsButRejectsBareShortLinks(string url, bool expected)
    {
        PoiUrlHelper.IsGoogleMapsUrl(url).Should().Be(expected);
    }

    [Fact]
    public void GetGoogleMapsUrl_PrefersExistingGoogleMapsUrl_OverCoordSearchFallback()
    {
        var poi = new Poi
        {
            Name = "Place",
            GoogleMapsUrl = "https://www.google.com/maps/place/X",
            Latitude = 50.0,
            Longitude = 19.0
        };

        PoiUrlHelper.GetGoogleMapsUrl(poi).Should().Be("https://www.google.com/maps/place/X");
    }

    [Fact]
    public void GetGoogleMapsUrl_FallsBackToCoordSearch_WhenNoUrlPresent()
    {
        var poi = new Poi { Name = "Place", Latitude = 50.0541, Longitude = 19.9354 };

        var url = PoiUrlHelper.GetGoogleMapsUrl(poi);

        url.Should().StartWith("https://www.google.com/maps/search/?api=1&query=");
        url.Should().Contain("50.0541,19.9354");
    }

    [Fact]
    public void GetGoogleMapsUrl_ReturnsHash_WhenNoUrlAndNoCoords()
    {
        var poi = new Poi { Name = "Nowhere" };
        PoiUrlHelper.GetGoogleMapsUrl(poi).Should().Be("#");
    }

    [Theory]
    [InlineData(double.NaN, 19.0)]
    [InlineData(50.0, double.NaN)]
    [InlineData(double.PositiveInfinity, 19.0)]
    [InlineData(50.0, double.NegativeInfinity)]
    public void GetGoogleMapsUrl_ReturnsHash_WhenCoordsAreNaNOrInfinity(double lat, double lon)
    {
        var poi = new Poi { Name = "Place", Latitude = lat, Longitude = lon };
        PoiUrlHelper.GetGoogleMapsUrl(poi).Should().Be("#");
    }

    [Fact]
    public void ExtractCoordinatesFromUrl_PrefersBangParams_OverAtSignFallback()
    {
        // !3d / !4d are the place anchor; @ is the viewport center.
        // When both are present, !3d/!4d wins.
        var url = "https://www.google.com/maps/place/X/@49.0,18.0,17z/data=!3d50.5!4d19.5";

        var coords = PoiUrlHelper.ExtractCoordinatesFromUrl(url);

        coords.Should().NotBeNull();
        coords!.Value.lat.Should().BeApproximately(50.5, 1e-6);
        coords.Value.lon.Should().BeApproximately(19.5, 1e-6);
    }

    [Fact]
    public void ExtractCoordinatesFromUrl_FallsBackToAtSign_WhenNoBangParams()
    {
        var url = "https://www.google.com/maps/@52.2297,21.0122,15z";

        var coords = PoiUrlHelper.ExtractCoordinatesFromUrl(url);

        coords.Should().NotBeNull();
        coords!.Value.lat.Should().BeApproximately(52.2297, 1e-6);
        coords.Value.lon.Should().BeApproximately(21.0122, 1e-6);
    }

    [Fact]
    public void ExtractPlaceCoordinatesFromUrl_RejectsViewportFallback()
    {
        // Strict variant: only !3d/!4d are accepted, never the @ viewport.
        var viewportOnly = "https://www.google.com/maps/@52.2297,21.0122,15z";
        PoiUrlHelper.ExtractPlaceCoordinatesFromUrl(viewportOnly).Should().BeNull();

        var withPlace = "https://www.google.com/maps/place/X/data=!3d50.5!4d19.5";
        PoiUrlHelper.ExtractPlaceCoordinatesFromUrl(withPlace).Should().NotBeNull();
    }

    [Fact]
    public void ExtractCoordinatesFromUrl_ReturnsNull_OnMalformedUrl()
    {
        PoiUrlHelper.ExtractCoordinatesFromUrl("https://example.com/no/coords").Should().BeNull();
        PoiUrlHelper.ExtractCoordinatesFromUrl("").Should().BeNull();
    }
}
