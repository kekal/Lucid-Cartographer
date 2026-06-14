using FluentAssertions;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Trip;
using Microsoft.Extensions.Options;

namespace LucidCartographer.Tests;

/// <summary>
/// Story 2.1 (AC 1, 2, 3) + Story 2.2 (AC 3, 4): the haversine Mock returns the
/// great-circle distance in meters and a duration = distance ÷ a PER-MODE assumed
/// speed in seconds, with null geometry + Source "Mock". TRIP-TRAVELMODE-01:
/// Any/Air carries Fidelity.Placeholder; Drive/Walk/Cycle carry Fidelity.Estimated.
/// </summary>
public class MockTravelTimeProviderTests
{
    private static readonly TravelEndpoint From = new(1, 50.0, 20.0);
    private static readonly TravelEndpoint To = new(2, 51.0, 21.0);

    private static MockTravelTimeProvider Provider(TravelTimeOptions options) =>
        new(Options.Create(options));

    private static MockTravelTimeProvider DefaultProvider() => Provider(new TravelTimeOptions
    {
        AssumedSpeedMetersPerSecond = 13.8889,
        DriveSpeedMetersPerSecond = 20.0,
        WalkSpeedMetersPerSecond = 1.4,
        CycleSpeedMetersPerSecond = 4.2,
    });

    [Fact]
    public void Attribution_IsNull_HaversineIsNotOsmDerived()
    {
        // TRIP-OSRM-02 (Story 4.2, AC4): the Mock declares no routing attribution —
        // a great-circle estimate is not OSM-derived, so only the base OSM tile
        // attribution applies under the default provider.
        DefaultProvider().Attribution.Should().BeNull();
    }

    [Fact]
    public async Task GetLeg_AnyAir_MatchesHaversineAndAnyAirSpeed_AndIsPlaceholder()
    {
        const double speed = 13.8889; // Any/Air assumed speed
        var expectedMeters = GeoUtils.HaversineDistance(From.Latitude, From.Longitude, To.Latitude, To.Longitude);
        var expectedSeconds = (int)Math.Round(expectedMeters / speed);

        var result = await DefaultProvider().GetLegAsync(From, To, TravelMode.AnyAir, CancellationToken.None);

        result.DistanceMeters.Should().BeApproximately(expectedMeters, 0.001);
        result.DurationSeconds.Should().Be(expectedSeconds);
        // TRIP-TRAVELMODE-01: Any/Air is Placeholder (UI shows "—"), never Estimated.
        result.Fidelity.Should().Be(Fidelity.Placeholder);
        result.GeometryPolyline.Should().BeNull();
    }

    [Theory]
    [InlineData(TravelMode.Drive)]
    [InlineData(TravelMode.Walk)]
    [InlineData(TravelMode.Cycle)]
    public async Task GetLeg_GroundModes_AreEstimated(string mode)
    {
        var result = await DefaultProvider().GetLegAsync(From, To, mode, CancellationToken.None);

        result.Fidelity.Should().Be(Fidelity.Estimated);
        result.GeometryPolyline.Should().BeNull();
    }

    [Fact]
    public async Task GetLeg_PerModeSpeed_ProducesDistinctDurations_ForSameDistance()
    {
        var options = new TravelTimeOptions
        {
            DriveSpeedMetersPerSecond = 20.0,
            CycleSpeedMetersPerSecond = 5.0,
            WalkSpeedMetersPerSecond = 1.0,
        };
        var provider = Provider(options);

        var drive = await provider.GetLegAsync(From, To, TravelMode.Drive, CancellationToken.None);
        var cycle = await provider.GetLegAsync(From, To, TravelMode.Cycle, CancellationToken.None);
        var walk = await provider.GetLegAsync(From, To, TravelMode.Walk, CancellationToken.None);

        // Same distance, slower mode ⇒ longer duration. Strictly increasing.
        drive.DistanceMeters.Should().Be(cycle.DistanceMeters).And.Be(walk.DistanceMeters);
        drive.DurationSeconds.Should().BeLessThan(cycle.DurationSeconds);
        cycle.DurationSeconds.Should().BeLessThan(walk.DurationSeconds);
    }

    [Fact]
    public void Source_IsMock()
    {
        DefaultProvider().Source.Should().Be("Mock");
        MockTravelTimeProvider.ProviderId.Should().Be("Mock");
    }

    [Fact]
    public async Task GetLeg_IdenticalEndpoints_ZeroDistanceZeroDuration()
    {
        var result = await DefaultProvider().GetLegAsync(From, From, TravelMode.AnyAir, CancellationToken.None);

        result.DistanceMeters.Should().Be(0);
        result.DurationSeconds.Should().Be(0);
        result.Fidelity.Should().Be(Fidelity.Placeholder);
        result.GeometryPolyline.Should().BeNull();
    }
}
