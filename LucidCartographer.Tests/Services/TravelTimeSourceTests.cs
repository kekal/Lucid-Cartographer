using System.Net.Http;
using FluentAssertions;
using LucidCartographer.Services;
using LucidCartographer.Services.Trip;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LucidCartographer.Tests;

/// <summary>
/// The <see cref="ITravelTimeProvider.ProducesMeasuredFidelity"/> capability seam, the
/// Valhalla provenance constant, and the ODbL attribution string. (The retired OSRM
/// source constant and its attribution string were removed in Epic 3, Story 3.3.)
/// </summary>
public class TravelTimeSourceTests
{
    [Fact]
    public void CapabilityFlag_DiscriminatesThroughInterface_MockFalse_MeasuredProviderTrue()
    {
        // AD-2: ProducesMeasuredFidelity is a real ITravelTimeProvider contract member that
        // discriminates by provider, not a Mock-only no-op. The estimate-only haversine Mock
        // returns false; the measured Valhalla provider (real road network) returns true.
        // Asserted through the interface type to lock the seam Story 2.3's recompute gate reads.
        ITravelTimeProvider mock = new MockTravelTimeProvider(Options.Create(new TravelTimeOptions()));
        ITravelTimeProvider measured = new ValhallaTravelTimeProvider(
            new NeverCalledHttpClientFactory(),
            Options.Create(new ValhallaOptions()),
            Options.Create(new TravelTimeOptions()),
            NullLogger<ValhallaTravelTimeProvider>.Instance);

        mock.ProducesMeasuredFidelity.Should().BeFalse();
        measured.ProducesMeasuredFidelity.Should().BeTrue();
    }

    // The capability flag is a pure property, so the provider never issues an HTTP request
    // in this test; the factory exists only to satisfy the constructor.
    private sealed class NeverCalledHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    [Fact]
    public void Valhalla_SourceConstant_IsValhalla()
    {
        TravelTimeSource.Valhalla.Should().Be("Valhalla");
    }

    [Fact]
    public void ValhallaAttribution_IsOdblAndNamesValhalla()
    {
        UiStrings.TripRoutingAttributionValhalla.Should().NotBeNullOrWhiteSpace();
        UiStrings.TripRoutingAttributionValhalla.Should().Contain("Valhalla");
        UiStrings.TripRoutingAttributionValhalla.Should().Contain("ODbL");
    }
}
