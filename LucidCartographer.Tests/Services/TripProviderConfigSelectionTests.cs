using FluentAssertions;
using LucidCartographer.Configuration;
using LucidCartographer.Services;
using LucidCartographer.Services.Trip;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LucidCartographer.Tests;

/// <summary>
/// Story 2.4 (AD-4): the config switch in AddTripServices(IConfiguration) selects the
/// active ITravelTimeProvider. TravelTime:Provider = "Valhalla" resolves the measured
/// Valhalla provider (and surfaces its ODbL attribution); missing / empty / "Mock" / any
/// non-Valhalla value (including the retired "Osrm" id) resolves the Mock with no routing
/// attribution (NFR-13 — Valhalla is opt-in, never the default).
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
        // The Valhalla provider ctor needs IHttpClientFactory; register it so the
        // config-selected provider resolves under either branch.
        services.AddHttpClient();
        services.AddTripServices(configuration);

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<ITravelTimeProvider>();
    }

    [Fact]
    public void ParameterlessOverload_RegistersMock_WithNoAttribution()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProviderShim.Instance));
        services.AddTripServices();

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<ITravelTimeProvider>();

        resolved.Should().BeOfType<MockTravelTimeProvider>();
        resolved.Attribution.Should().BeNull();
    }

    [Fact]
    public void Provider_Valhalla_ResolvesValhallaProvider_AndSurfacesAttribution()
    {
        var resolved = ResolveProvider(new Dictionary<string, string?>
        {
            ["TravelTime:Provider"] = "Valhalla",
            ["TravelTime:Valhalla:BaseUrl"] = "http://valhalla:8002",
        });

        resolved.Should().BeOfType<ValhallaTravelTimeProvider>();
        resolved.Source.Should().Be(TravelTimeSource.Valhalla);
        resolved.ProducesMeasuredFidelity.Should().BeTrue();
        resolved.Attribution.Should().NotBeNull();
        resolved.Attribution.Should().Be(UiStrings.TripRoutingAttributionValhalla);
    }

    [Theory]
    [InlineData("valhalla")]
    [InlineData("VALHALLA")]
    [InlineData("VaLhAlLa")]
    public void Provider_Valhalla_IsCaseInsensitive(string value)
    {
        // The branch matches with StringComparison.OrdinalIgnoreCase, so any casing
        // of "Valhalla" must still select the measured provider (AC 1).
        var resolved = ResolveProvider(new Dictionary<string, string?>
        {
            ["TravelTime:Provider"] = value,
        });

        resolved.Should().BeOfType<ValhallaTravelTimeProvider>();
    }

    [Fact]
    public void Provider_Valhalla_BindsValhallaOptionsFromConfig()
    {
        // AC 1a: the branch binds ValhallaOptions from TravelTime:Valhalla, so custom
        // BaseUrl / RequestTimeoutSeconds / GeometryPrecision values are picked up
        // (not just the class defaults).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TravelTime:Provider"] = "Valhalla",
                ["TravelTime:Valhalla:BaseUrl"] = "http://custom-valhalla:9999",
                ["TravelTime:Valhalla:RequestTimeoutSeconds"] = "42",
                ["TravelTime:Valhalla:GeometryPrecision"] = "5",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProviderShim.Instance));
        services.AddHttpClient();
        services.AddTripServices(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ValhallaOptions>>().Value;

        options.BaseUrl.Should().Be("http://custom-valhalla:9999");
        options.RequestTimeoutSeconds.Should().Be(42);
        options.GeometryPrecision.Should().Be(5);
    }

    [Fact]
    public void Provider_Valhalla_NamedHttpClient_UsesConfiguredTimeout()
    {
        // AC 1b (closes Story 2.2's deferred timeout wiring): the named "valhalla"
        // client is registered with client.Timeout taken from RequestTimeoutSeconds.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TravelTime:Provider"] = "Valhalla",
                ["TravelTime:Valhalla:RequestTimeoutSeconds"] = "37",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProviderShim.Instance));
        services.AddHttpClient();
        services.AddTripServices(configuration);

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var client = factory.CreateClient(ValhallaTravelTimeProvider.HttpClientName);

        client.Timeout.Should().Be(TimeSpan.FromSeconds(37));
    }

    [Fact]
    public void Provider_Valhalla_NamedHttpClient_ClampsNonPositiveTimeout()
    {
        // AC 1b: the wiring clamps with Math.Max(1, …), so a non-positive configured
        // timeout becomes 1 second rather than an invalid zero/negative HttpClient.Timeout.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TravelTime:Provider"] = "Valhalla",
                ["TravelTime:Valhalla:RequestTimeoutSeconds"] = "0",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProviderShim.Instance));
        services.AddHttpClient();
        services.AddTripServices(configuration);

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var client = factory.CreateClient(ValhallaTravelTimeProvider.HttpClientName);

        client.Timeout.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Provider_Missing_ResolvesMock_WithNoAttribution()
    {
        var resolved = ResolveProvider(new Dictionary<string, string?>());
        resolved.Should().BeOfType<MockTravelTimeProvider>();
        resolved.Attribution.Should().BeNull();
    }

    [Theory]
    [InlineData("Mock")]
    [InlineData("")]
    [InlineData("Osrm")]
    [InlineData("something-else")]
    public void Provider_MockEmptyOrRetired_ResolvesMock_WithNoAttribution(string value)
    {
        var resolved = ResolveProvider(new Dictionary<string, string?>
        {
            ["TravelTime:Provider"] = value,
        });

        resolved.Should().BeOfType<MockTravelTimeProvider>();
        resolved.Attribution.Should().BeNull();
    }

    // --- Story 3.1 (FR-15, AD-7): retired/unknown provider id classification ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Mock")]
    [InlineData("mock")]
    public void ClassifyProvider_DefaultIds_ResolveDefault(string? value)
    {
        TripServicesExtensions.ClassifyProvider(value)
            .Should().Be(TripServicesExtensions.ProviderSelection.Default);
    }

    [Theory]
    [InlineData("Valhalla")]
    [InlineData("valhalla")]
    [InlineData("VALHALLA")]
    public void ClassifyProvider_ValhallaIds_ResolveValhalla(string value)
    {
        TripServicesExtensions.ClassifyProvider(value)
            .Should().Be(TripServicesExtensions.ProviderSelection.Valhalla);
    }

    [Theory]
    [InlineData("Osrm")]
    [InlineData("OSRM")]
    [InlineData("osrm")]
    [InlineData("Graphhopper")]
    [InlineData("something-else")]
    public void ClassifyProvider_RetiredOrUnknownIds_ResolveRetiredOrUnknown(string value)
    {
        // FR-15: the retired "Osrm" id (and any unrecognized value) is classified as
        // RetiredOrUnknown, which the caller surfaces as warn-and-fall-back, never fail-fast.
        TripServicesExtensions.ClassifyProvider(value)
            .Should().Be(TripServicesExtensions.ProviderSelection.RetiredOrUnknown);
    }

    [Fact]
    public void Provider_Retired_FallsBackToMock_AndDoesNotThrow()
    {
        // FR-15 / AD-7: a retired id never bricks boot — the host wires up cleanly and the
        // active provider is the smart-haversine default. (The prominent warning itself is
        // emitted via a bootstrap console logger at selection time; its classification is
        // asserted by ClassifyProvider_RetiredOrUnknownIds_* above.)
        var act = () => ResolveProvider(new Dictionary<string, string?>
        {
            ["TravelTime:Provider"] = "Osrm",
        });

        act.Should().NotThrow();
        act().Should().BeOfType<MockTravelTimeProvider>();
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
