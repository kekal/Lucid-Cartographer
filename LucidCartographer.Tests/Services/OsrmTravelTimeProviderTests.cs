using System.Net;
using System.Text;
using FluentAssertions;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Trip;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LucidCartographer.Tests;

/// <summary>
/// Story 4.1 (TRIP-OSRM-01, AC1/AC2/AC3/AC6): the OSRM provider drives a single
/// per-leg /route query over a stubbed HttpMessageHandler (no real OSRM, no
/// network). Covers: a successful Measured route with the right profile token +
/// lon,lat order; no-route / empty-routes / HTTP-500 / connection-error / timeout
/// ⇒ throw (degradation belongs to the background-service catch, not here); Any/Air
/// ⇒ no HTTP + Placeholder; an unconfigured profile ⇒ throw; Source == "OSRM"; and
/// real cancellation re-throws OperationCanceledException.
/// </summary>
public class OsrmTravelTimeProviderTests
{
    private static readonly TravelEndpoint From = new(1, 50.0, 20.0);
    private static readonly TravelEndpoint To = new(2, 51.5, 21.5);

    // A real OSRM /route success body (encoded-polyline geometry).
    private const string OkBody =
        "{\"code\":\"Ok\",\"routes\":[{\"duration\":1234.6,\"distance\":56789.0,\"geometry\":\"_p~iF~ps|U_ulLnnqC\"}]}";

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri;
            return Task.FromResult(responder(request, cancellationToken));
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static OsrmOptions AllProfilesConfigured() => new()
    {
        DriveBaseUrl = "http://osrm-car:5000",
        WalkBaseUrl = "http://osrm-foot:5000",
        CycleBaseUrl = "http://osrm-bike:5000",
    };

    private static OsrmTravelTimeProvider Build(StubHandler handler, OsrmOptions? osrm = null) =>
        new(
            new StubHttpClientFactory(handler),
            Options.Create(osrm ?? AllProfilesConfigured()),
            Options.Create(new TravelTimeOptions()),
            NullLogger<OsrmTravelTimeProvider>.Instance);

    [Fact]
    public void Source_IsOsrm()
    {
        var provider = Build(new StubHandler((_, _) => Json(HttpStatusCode.OK, OkBody)));
        provider.Source.Should().Be("OSRM");
        provider.Source.Should().Be(TravelTimeSource.Osrm);
    }

    [Theory]
    [InlineData(TravelMode.Drive, "car")]
    [InlineData(TravelMode.Walk, "foot")]
    [InlineData(TravelMode.Cycle, "bike")]
    public async Task GetLeg_Success_ReturnsMeasured_WithProfileToken_AndLonLatOrder(string mode, string profile)
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, OkBody));
        var provider = Build(handler);

        var result = await provider.GetLegAsync(From, To, mode, CancellationToken.None);

        result.Fidelity.Should().Be(Fidelity.Measured);
        result.DurationSeconds.Should().Be(1235, "duration is rounded to whole seconds");
        result.DistanceMeters.Should().Be(56789.0);
        result.GeometryPolyline.Should().Be("_p~iF~ps|U_ulLnnqC", "the encoded polyline is stored verbatim");

        var url = handler.LastRequestUri!.ToString();
        url.Should().Contain($"/route/v1/{profile}/", "the per-mode OSRM profile token is in the path");
        // OSRM convention: lon,lat;lon,lat — longitude FIRST.
        url.Should().Contain("20,50;21.5,51.5", "coordinates are lon,lat (invariant-culture formatted)");
        url.Should().Contain("geometries=polyline");
        url.Should().Contain("overview=full");
    }

    [Fact]
    public async Task GetLeg_AnyAir_MakesNoHttpCall_AndIsPlaceholder()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, OkBody));
        var provider = Build(handler);

        var result = await provider.GetLegAsync(From, To, TravelMode.AnyAir, CancellationToken.None);

        handler.CallCount.Should().Be(0, "Any/Air is never routed by OSRM — no HTTP call");
        result.Fidelity.Should().Be(Fidelity.Placeholder);
        result.GeometryPolyline.Should().BeNull("Air carries no road geometry");
        result.DistanceMeters.Should().BeGreaterThan(0, "a straight-line distance is still computed");
    }

    [Fact]
    public async Task GetLeg_CodeNotOk_Throws()
    {
        var handler = new StubHandler((_, _) =>
            Json(HttpStatusCode.OK, "{\"code\":\"NoRoute\",\"routes\":[]}"));
        var provider = Build(handler);

        var act = async () => await provider.GetLegAsync(From, To, TravelMode.Drive, CancellationToken.None);

        (await act.Should().ThrowAsync<OsrmRouteUnavailableException>())
            .Which.Message.Should().Contain("NoRoute");
    }

    [Fact]
    public async Task GetLeg_EmptyRoutes_Throws()
    {
        var handler = new StubHandler((_, _) =>
            Json(HttpStatusCode.OK, "{\"code\":\"Ok\",\"routes\":[]}"));
        var provider = Build(handler);

        var act = async () => await provider.GetLegAsync(From, To, TravelMode.Drive, CancellationToken.None);

        await act.Should().ThrowAsync<OsrmRouteUnavailableException>();
    }

    [Fact]
    public async Task GetLeg_OkButNoGeometry_Throws()
    {
        // A Measured leg must carry geometry (AC1). A "code":"Ok" route with the
        // geometry omitted/blank must degrade (throw), not persist a geometry-less
        // Measured row that 4.2 can't draw and recompute can't fix.
        var handler = new StubHandler((_, _) =>
            Json(HttpStatusCode.OK, "{\"code\":\"Ok\",\"routes\":[{\"duration\":100.0,\"distance\":2000.0,\"geometry\":\"\"}]}"));
        var provider = Build(handler);

        var act = async () => await provider.GetLegAsync(From, To, TravelMode.Drive, CancellationToken.None);

        (await act.Should().ThrowAsync<OsrmRouteUnavailableException>())
            .Which.Message.Should().Contain("no geometry");
    }

    [Fact]
    public async Task GetLeg_Http500_Throws()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.InternalServerError, "boom"));
        var provider = Build(handler);

        var act = async () => await provider.GetLegAsync(From, To, TravelMode.Drive, CancellationToken.None);

        (await act.Should().ThrowAsync<OsrmRouteUnavailableException>())
            .Which.Message.Should().Contain("500");
    }

    [Fact]
    public async Task GetLeg_HttpRequestException_Throws()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("connection refused"));
        var provider = Build(handler);

        var act = async () => await provider.GetLegAsync(From, To, TravelMode.Drive, CancellationToken.None);

        await act.Should().ThrowAsync<OsrmRouteUnavailableException>();
    }

    [Fact]
    public async Task GetLeg_Timeout_Throws_WhenCallerTokenNotCancelled()
    {
        // HttpClient surfaces its own Timeout as a TaskCanceledException whose token
        // is NOT the caller's. Simulate that: throw OperationCanceledException with a
        // token that is not the (uncancelled) caller token.
        var handler = new StubHandler((_, _) =>
            throw new TaskCanceledException("timed out", new TimeoutException()));
        var provider = Build(handler);

        var act = async () => await provider.GetLegAsync(From, To, TravelMode.Drive, CancellationToken.None);

        await act.Should().ThrowAsync<OsrmRouteUnavailableException>();
    }

    [Fact]
    public async Task GetLeg_RealCancellation_RethrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler((_, token) =>
        {
            cts.Cancel();
            // Throw cancellation tied to the CALLER's token (a genuine cancellation).
            throw new OperationCanceledException(cts.Token);
        });
        var provider = Build(handler);

        var act = async () => await provider.GetLegAsync(From, To, TravelMode.Drive, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetLeg_ProfileWithNoConfiguredUrl_Throws_NoHttpCall()
    {
        // Only Drive is configured; Walk has no URL ⇒ no coverage ⇒ throw (degrade).
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, OkBody));
        var provider = Build(handler, new OsrmOptions { DriveBaseUrl = "http://osrm-car:5000" });

        var act = async () => await provider.GetLegAsync(From, To, TravelMode.Walk, CancellationToken.None);

        await act.Should().ThrowAsync<OsrmRouteUnavailableException>();
        handler.CallCount.Should().Be(0, "an unconfigured profile never reaches the network");
    }
}
