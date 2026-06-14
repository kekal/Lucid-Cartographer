using FluentAssertions;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Trip;
using Microsoft.Extensions.Options;

namespace LucidCartographer.Tests;

/// <summary>
/// Story 2.3 (AC1, TRIP-DEGRADE-01): the shared <see cref="EstimatedTravelTime"/>
/// helper is the single haversine straight-line estimate code path. These prove the
/// refactor is behaviour-preserving — for the same inputs the helper's ground-mode
/// output equals the Mock's — plus per-mode speed and the zero-distance edge.
/// </summary>
public class EstimatedTravelTimeTests
{
    private static readonly TravelEndpoint From = new(1, 50.0, 20.0);
    private static readonly TravelEndpoint To = new(2, 51.0, 21.0);

    private static TravelTimeOptions Options() => new()
    {
        AssumedSpeedMetersPerSecond = 13.8889,
        DriveSpeedMetersPerSecond = 20.0,
        WalkSpeedMetersPerSecond = 1.4,
        CycleSpeedMetersPerSecond = 4.2,
    };

    [Theory]
    [InlineData(TravelMode.Drive)]
    [InlineData(TravelMode.Walk)]
    [InlineData(TravelMode.Cycle)]
    public async Task Compute_MatchesMockGroundModeOutput_ForSameInputs(string mode)
    {
        var options = Options();
        var mock = new MockTravelTimeProvider(Microsoft.Extensions.Options.Options.Create(options));

        var helper = EstimatedTravelTime.Compute(From, To, mode, options);
        var fromMock = await mock.GetLegAsync(From, To, mode, CancellationToken.None);

        // The Mock reuses the helper for ground modes ⇒ identical result.
        helper.DurationSeconds.Should().Be(fromMock.DurationSeconds);
        helper.DistanceMeters.Should().Be(fromMock.DistanceMeters);
        helper.Fidelity.Should().Be(fromMock.Fidelity).And.Be(Fidelity.Estimated);
        helper.GeometryPolyline.Should().BeNull();
    }

    [Fact]
    public void Compute_MatchesExpectedHaversineAndPerModeSpeed()
    {
        var options = Options();
        var expectedMeters = GeoUtils.HaversineDistance(From.Latitude, From.Longitude, To.Latitude, To.Longitude);

        var drive = EstimatedTravelTime.Compute(From, To, TravelMode.Drive, options);

        drive.DistanceMeters.Should().BeApproximately(expectedMeters, 0.001);
        drive.DurationSeconds.Should().Be((int)Math.Round(expectedMeters / options.DriveSpeedMetersPerSecond));
        // Always Estimated — even for Any/Air (the Any/Air ⇒ Placeholder re-badge is
        // the Mock's responsibility, never the shared helper's; the fallback never
        // routes Any/Air here anyway).
        drive.Fidelity.Should().Be(Fidelity.Estimated);
    }

    [Fact]
    public void Compute_PerModeSpeed_ProducesStrictlyIncreasingDurations()
    {
        var options = new TravelTimeOptions
        {
            DriveSpeedMetersPerSecond = 20.0,
            CycleSpeedMetersPerSecond = 5.0,
            WalkSpeedMetersPerSecond = 1.0,
        };

        var drive = EstimatedTravelTime.Compute(From, To, TravelMode.Drive, options);
        var cycle = EstimatedTravelTime.Compute(From, To, TravelMode.Cycle, options);
        var walk = EstimatedTravelTime.Compute(From, To, TravelMode.Walk, options);

        drive.DistanceMeters.Should().Be(cycle.DistanceMeters).And.Be(walk.DistanceMeters);
        drive.DurationSeconds.Should().BeLessThan(cycle.DurationSeconds);
        cycle.DurationSeconds.Should().BeLessThan(walk.DurationSeconds);
    }

    [Fact]
    public void Compute_ZeroDistance_ZeroDurationZeroDistance()
    {
        var result = EstimatedTravelTime.Compute(From, From, TravelMode.Drive, Options());

        result.DistanceMeters.Should().Be(0);
        result.DurationSeconds.Should().Be(0);
        result.Fidelity.Should().Be(Fidelity.Estimated);
        result.GeometryPolyline.Should().BeNull();
    }
}
