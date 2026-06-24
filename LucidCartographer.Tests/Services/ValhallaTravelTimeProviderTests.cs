using System.Globalization;
using System.Net;
using System.Text;
using FluentAssertions;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Trip;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LucidCartographer.Tests;

/// <summary>
/// Story 2.2 (AD-3): the Valhalla provider drives a single per-leg /route POST over a
/// stubbed HttpMessageHandler (no real Valhalla, no network). Covers: a successful
/// Measured route with the right costing token + {lat,lon} invariant-culture body; the
/// km→m and seconds-rounded edge conversions; verbatim polyline6 geometry; Any/Air ⇒ no
/// HTTP + Placeholder; an unsupported mode + no-route / empty-legs / blank-shape / HTTP-500
/// / connection-error / timeout ⇒ throw ValhallaRouteUnavailableException; Source ==
/// "Valhalla"; ProducesMeasuredFidelity == true; and real cancellation re-throws.
/// </summary>
public class ValhallaTravelTimeProviderTests
{
    private static readonly TravelEndpoint From = new(1, 50.0, 20.0);
    private static readonly TravelEndpoint To = new(2, 51.5, 21.5);

    // A real Valhalla /route success body: status 0, summary.time seconds, summary.length km,
    // legs[].shape polyline6-encoded geometry.
    private const string OkBody =
        "{\"trip\":{\"status\":0,\"summary\":{\"time\":1234.6,\"length\":56.789},\"legs\":[{\"shape\":\"_p~iF~ps|U_ulLnnqC\"}]}}";

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri;
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return responder(request, cancellationToken);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static ValhallaTravelTimeProvider Build(StubHandler handler, ValhallaOptions? valhalla = null) =>
        new(
            new StubHttpClientFactory(handler),
            Options.Create(valhalla ?? new ValhallaOptions()),
            Options.Create(new TravelTimeOptions()),
            NullLogger<ValhallaTravelTimeProvider>.Instance);

    [Fact]
    public void Source_IsValhalla()
    {
        var provider = Build(new StubHandler((_, _) => Json(HttpStatusCode.OK, OkBody)));
        provider.Source.Should().Be("Valhalla");
        provider.Source.Should().Be(TravelTimeSource.Valhalla);
    }

    [Fact]
    public void Attribution_IsValhallaOdblString()
    {
        var provider = Build(new StubHandler((_, _) => Json(HttpStatusCode.OK, OkBody)));
        provider.Attribution.Should().Be(Services.UiStrings.TripRoutingAttributionValhalla);
        provider.Attribution.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ProducesMeasuredFidelity_IsTrue()
    {
        var provider = Build(new StubHandler((_, _) => Json(HttpStatusCode.OK, OkBody)));
        provider.ProducesMeasuredFidelity.Should().BeTrue();
    }

    [Theory]
    [InlineData(TravelMode.Drive, "auto")]
    [InlineData(TravelMode.Walk, "pedestrian")]
    [InlineData(TravelMode.Cycle, "bicycle")]
    public async Task GetLeg_Success_ReturnsMeasured_WithCostingToken(string mode, string costing)
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, OkBody));
        var provider = Build(handler);

        var result = await provider.GetLegAsync(From, To, mode, CancellationToken.None);

        result.Fidelity.Should().Be(Fidelity.Measured);
        handler.LastRequestUri!.ToString().Should().EndWith("/route", "Valhalla posts to a single /route endpoint");

        var body = handler.LastRequestBody!;
        body.Should().Contain($"\"costing\":\"{costing}\"", "the ground mode maps to its Valhalla costing token");
    }

    [Fact]
    public async Task GetLeg_Success_ConvertsSecondsAndKmToMeters()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, OkBody));
        var provider = Build(handler);

        var result = await provider.GetLegAsync(From, To, TravelMode.Drive, CancellationToken.None);

        result.DurationSeconds.Should().Be(1235, "summary.time is rounded to whole seconds");
        result.DistanceMeters.Should().Be(56789.0, "summary.length km is ×1000 to meters at the edge");
    }

    [Fact]
    public async Task GetLeg_Success_ReturnsGeometryVerbatim()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, OkBody));
        var provider = Build(handler);

        var result = await provider.GetLegAsync(From, To, TravelMode.Drive, CancellationToken.None);

        result.GeometryPolyline.Should().Be(
            "_p~iF~ps|U_ulLnnqC", "the precision-6 encoded polyline is stored as-is; decode happens in JS");
    }

    [Fact]
    public async Task GetLeg_BodyUsesLatLonAndInvariantCulture_UnderCommaDecimalCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // A comma-decimal locale would corrupt manually built coordinate strings (AD-3 trap).
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, OkBody));
            var provider = Build(handler);

            await provider.GetLegAsync(From, To, TravelMode.Drive, CancellationToken.None);

            var body = handler.LastRequestBody!;
            body.Should().Contain("\"lat\":50", "coordinates are {lat,lon} (lat first, opposite OSRM)");
            body.Should().Contain("\"lon\":20");
            body.Should().Contain("\"lat\":51.5", "decimals use '.' regardless of thread culture");
            body.Should().Contain("\"lon\":21.5");
            body.Should().NotContain("50,0", "no comma-decimal corruption");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task GetLeg_AnyAir_MakesNoHttpCall_AndIsPlaceholder()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, OkBody));
        var provider = Build(handler);

        var result = await provider.GetLegAsync(From, To, TravelMode.AnyAir, CancellationToken.None);

        handler.CallCount.Should().Be(0, "Any/Air is never routed by Valhalla — no HTTP call");
        result.Fidelity.Should().Be(Fidelity.Placeholder);
        result.GeometryPolyline.Should().BeNull("Air carries no road geometry");
        result.DistanceMeters.Should().BeGreaterThan(0, "a straight-line distance is still computed");
    }

    [Fact]
    public async Task GetLeg_UnsupportedMode_Throws_NoHttpCall()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, OkBody));
        var provider = Build(handler);

        var act = async () => await provider.GetLegAsync(From, To, "Teleport", CancellationToken.None);

        await act.Should().ThrowAsync<ValhallaRouteUnavailableException>();
        handler.CallCount.Should().Be(0, "an unsupported costing never reaches the network");
    }

    [Fact]
    public async Task GetLeg_StatusNotZero_Throws()
    {
        var handler = new StubHandler((_, _) =>
            Json(HttpStatusCode.OK, "{\"trip\":{\"status\":442,\"summary\":{\"time\":0,\"length\":0},\"legs\":[]}}"));
        var provider = Build(handler);

        var act = async () => await provider.GetLegAsync(From, To, TravelMode.Drive, CancellationToken.None);

        (await act.Should().ThrowAsync<ValhallaRouteUnavailableException>())
            .Which.Message.Should().Contain("442");
    }

    [Fact]
    public async Task GetLeg_MissingTrip_Throws()
    {
        // No `trip` object at all (Valhalla 4xx error envelopes have no trip) — the parsed
        // trip is null, a distinct MapResponse branch from a present-but-nonzero status.
        var handler = new StubHandler((_, _) =>
            Json(HttpStatusCode.OK, "{\"error\":\"No path could be found for input\"}"));
        var provider = Build(handler);

        var act = async () => await provider.GetLegAsync(From, To, TravelMode.Drive, CancellationToken.None);

        (await act.Should().ThrowAsync<ValhallaRouteUnavailableException>())
            .Which.Message.Should().Contain("(none)", "a missing trip reports the '(none)' status sentinel");
    }

    [Fact]
    public async Task GetLeg_StatusZero_MissingSummary_Throws()
    {
        // status 0 but no summary block — a distinct guard from empty-legs / nonzero-status.
        var handler = new StubHandler((_, _) =>
            Json(HttpStatusCode.OK, "{\"trip\":{\"status\":0,\"legs\":[{\"shape\":\"_p~iF~ps|U\"}]}}"));
        var provider = Build(handler);

        var act = async () => await provider.GetLegAsync(From, To, TravelMode.Drive, CancellationToken.None);

        (await act.Should().ThrowAsync<ValhallaRouteUnavailableException>())
            .Which.Message.Should().Contain("summary");
    }

    [Fact]
    public async Task GetLeg_EmptyLegs_Throws()
    {
        var handler = new StubHandler((_, _) =>
            Json(HttpStatusCode.OK, "{\"trip\":{\"status\":0,\"summary\":{\"time\":100,\"length\":2},\"legs\":[]}}"));
        var provider = Build(handler);

        var act = async () => await provider.GetLegAsync(From, To, TravelMode.Drive, CancellationToken.None);

        await act.Should().ThrowAsync<ValhallaRouteUnavailableException>();
    }

    [Fact]
    public async Task GetLeg_BlankShape_Throws()
    {
        var handler = new StubHandler((_, _) =>
            Json(HttpStatusCode.OK, "{\"trip\":{\"status\":0,\"summary\":{\"time\":100,\"length\":2},\"legs\":[{\"shape\":\"\"}]}}"));
        var provider = Build(handler);

        var act = async () => await provider.GetLegAsync(From, To, TravelMode.Drive, CancellationToken.None);

        (await act.Should().ThrowAsync<ValhallaRouteUnavailableException>())
            .Which.Message.Should().Contain("no geometry");
    }

    [Fact]
    public async Task GetLeg_WhitespaceShape_Throws()
    {
        // A whitespace-only shape (not just empty "") still trips the IsNullOrWhiteSpace
        // measured-geometry guard — a geometry-less Measured row must never persist.
        var handler = new StubHandler((_, _) =>
            Json(HttpStatusCode.OK, "{\"trip\":{\"status\":0,\"summary\":{\"time\":100,\"length\":2},\"legs\":[{\"shape\":\"   \"}]}}"));
        var provider = Build(handler);

        var act = async () => await provider.GetLegAsync(From, To, TravelMode.Drive, CancellationToken.None);

        (await act.Should().ThrowAsync<ValhallaRouteUnavailableException>())
            .Which.Message.Should().Contain("no geometry");
    }

    [Fact]
    public async Task GetLeg_TargetsConfiguredBaseUrl_WithTrailingSlashTrimmed()
    {
        // NFR7 single-host: the request must hit only the one configured BaseUrl, and a
        // trailing slash in config must not double up to "//route".
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, OkBody));
        var provider = Build(handler, new ValhallaOptions { BaseUrl = "http://valhalla.internal:9999/" });

        await provider.GetLegAsync(From, To, TravelMode.Drive, CancellationToken.None);

        handler.LastRequestUri!.ToString().Should().Be(
            "http://valhalla.internal:9999/route",
            "the single configured host is honored and the trailing slash is trimmed (no //route)");
    }

    [Fact]
    public async Task GetLeg_Http500_Throws()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.InternalServerError, "boom"));
        var provider = Build(handler);

        var act = async () => await provider.GetLegAsync(From, To, TravelMode.Drive, CancellationToken.None);

        (await act.Should().ThrowAsync<ValhallaRouteUnavailableException>())
            .Which.Message.Should().Contain("500");
    }

    [Fact]
    public async Task GetLeg_UnparseableResponse_Throws()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, "not json at all"));
        var provider = Build(handler);

        var act = async () => await provider.GetLegAsync(From, To, TravelMode.Drive, CancellationToken.None);

        await act.Should().ThrowAsync<ValhallaRouteUnavailableException>();
    }

    [Fact]
    public async Task GetLeg_HttpRequestException_Throws()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("connection refused"));
        var provider = Build(handler);

        var act = async () => await provider.GetLegAsync(From, To, TravelMode.Drive, CancellationToken.None);

        await act.Should().ThrowAsync<ValhallaRouteUnavailableException>();
    }

    [Fact]
    public async Task GetLeg_Timeout_Throws_WhenCallerTokenNotCancelled()
    {
        // HttpClient surfaces its own Timeout as a TaskCanceledException whose token is NOT
        // the caller's. Simulate that with an uncancelled caller token.
        var handler = new StubHandler((_, _) =>
            throw new TaskCanceledException("timed out", new TimeoutException()));
        var provider = Build(handler);

        var act = async () => await provider.GetLegAsync(From, To, TravelMode.Drive, CancellationToken.None);

        await act.Should().ThrowAsync<ValhallaRouteUnavailableException>();
    }

    [Fact]
    public async Task GetLeg_RealCancellation_RethrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler((_, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var provider = Build(handler);

        var act = async () => await provider.GetLegAsync(From, To, TravelMode.Drive, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
