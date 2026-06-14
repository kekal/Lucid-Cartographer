using FluentAssertions;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Trip;
using Microsoft.Extensions.Options;

namespace LucidCartographer.Tests;

/// <summary>
/// Story 2.1 (AC 1, 2, 3): the haversine Mock returns the great-circle distance
/// in meters and a duration = distance ÷ assumed speed in seconds, always
/// Fidelity.Estimated with null geometry, Source "Mock".
/// </summary>
public class MockTravelTimeProviderTests
{
    private static MockTravelTimeProvider Provider(double speedMps) =>
        new(Options.Create(new TravelTimeOptions { AssumedSpeedMetersPerSecond = speedMps }));

    [Fact]
    public async Task GetLeg_KnownCoords_MatchesHaversineAndAssumedSpeed()
    {
        // Two arbitrary placeable points.
        var from = new TravelEndpoint(1, 50.0, 20.0);
        var to = new TravelEndpoint(2, 51.0, 21.0);
        const double speed = 13.8889; // ~50 km/h

        var expectedMeters = GeoUtils.HaversineDistance(from.Latitude, from.Longitude, to.Latitude, to.Longitude);
        var expectedSeconds = (int)Math.Round(expectedMeters / speed);

        var result = await Provider(speed).GetLegAsync(from, to, TravelMode.AnyAir, CancellationToken.None);

        result.DistanceMeters.Should().BeApproximately(expectedMeters, 0.001);
        result.DurationSeconds.Should().Be(expectedSeconds);
        result.Fidelity.Should().Be(Fidelity.Estimated);
        result.GeometryPolyline.Should().BeNull();
    }

    [Fact]
    public void Source_IsMock()
    {
        Provider(13.8889).Source.Should().Be("Mock");
        MockTravelTimeProvider.ProviderId.Should().Be("Mock");
    }

    [Fact]
    public async Task GetLeg_IdenticalEndpoints_ZeroDistanceZeroDuration()
    {
        var p = new TravelEndpoint(1, 50.0, 20.0);

        var result = await Provider(13.8889).GetLegAsync(p, p, TravelMode.AnyAir, CancellationToken.None);

        result.DistanceMeters.Should().Be(0);
        result.DurationSeconds.Should().Be(0);
        result.Fidelity.Should().Be(Fidelity.Estimated);
        result.GeometryPolyline.Should().BeNull();
    }
}
