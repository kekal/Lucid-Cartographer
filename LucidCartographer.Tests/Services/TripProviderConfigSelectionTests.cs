using FluentAssertions;
using LucidCartographer.Configuration;
using LucidCartographer.Services.Trip;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LucidCartographer.Tests;

/// <summary>
/// Story 4.1 (TRIP-OSRM-01, AC1): the config switch in AddTripServices(IConfiguration)
/// selects the active ITravelTimeProvider. TravelTime:Provider = "Osrm" resolves the
/// OSRM provider; missing / "Mock" / an unrecognized value resolves the Mock (NFR9 —
/// OSRM is opt-in, never the default).
/// </summary>
public class TripProviderConfigSelectionTests
{
    private static ITravelTimeProvider ResolveProvider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProviderShim.Instance));
        services.AddTripServices(configuration);

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<ITravelTimeProvider>();
    }

    [Fact]
    public void Provider_Osrm_ResolvesOsrmProvider()
    {
        var resolved = ResolveProvider(new Dictionary<string, string?>
        {
            ["TravelTime:Provider"] = "Osrm",
            ["TravelTime:Osrm:DriveBaseUrl"] = "http://osrm-car:5000",
        });

        resolved.Should().BeOfType<OsrmTravelTimeProvider>();
        resolved.Source.Should().Be(TravelTimeSource.Osrm);
    }

    [Fact]
    public void Provider_Missing_ResolvesMock()
    {
        var resolved = ResolveProvider(new Dictionary<string, string?>());
        resolved.Should().BeOfType<MockTravelTimeProvider>();
    }

    [Theory]
    [InlineData("Mock")]
    [InlineData("something-else")]
    public void Provider_MockOrUnknown_ResolvesMock(string value)
    {
        var resolved = ResolveProvider(new Dictionary<string, string?>
        {
            ["TravelTime:Provider"] = value,
        });

        resolved.Should().BeOfType<MockTravelTimeProvider>();
    }

    // Minimal ILoggerProvider so AddTripServices' hosted-service / provider ctors can
    // resolve ILogger<T> without pulling in a real logging backend.
    private sealed class NullLoggerProviderShim : ILoggerProvider
    {
        public static readonly NullLoggerProviderShim Instance = new();
        public ILogger CreateLogger(string categoryName) =>
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public void Dispose() { }
    }
}
