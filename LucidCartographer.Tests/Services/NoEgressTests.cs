using System.Net;
using System.Reflection;
using System.Text;
using FluentAssertions;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Trip;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LucidCartographer.Tests;

/// <summary>
/// Story 2.6 (AD-5, NFR7 — HARD privacy constraint): an automated no-egress proof that stop
/// coordinates never leave the deployment at any fidelity rung.
///
/// Two halves, mirroring the established <see cref="ValhallaTravelTimeProviderTests"/> stub seam
/// (a <c>StubHandler</c> capturing <c>CallCount</c> + <c>LastRequestUri</c>, injected via a
/// <c>StubHttpClientFactory</c>) — no real network, no live Valhalla:
/// <list type="bullet">
/// <item>The <b>default</b> provider (smart-haversine, <see cref="MockTravelTimeProvider"/>)
/// computes a ground leg <b>in-process</b> and is structurally incapable of an out-call: its
/// constructor takes only <see cref="IOptions{TravelTimeOptions}"/> — no
/// <see cref="IHttpClientFactory"/>/<see cref="HttpClient"/> dependency.</item>
/// <item>The <see cref="ValhallaTravelTimeProvider"/> contacts <b>only</b> the single configured
/// internal base-URL host (the captured request URI's host+port equals the configured
/// <c>BaseUrl</c> — no other host is ever contacted) for a ground leg, and issues <b>no</b> HTTP
/// at all for an Air/AnyAir leg.</item>
/// </list>
/// </summary>
public class NoEgressTests
{
    private static readonly TravelEndpoint From = new(1, 50.0, 20.0);
    private static readonly TravelEndpoint To = new(2, 51.5, 21.5);

    // A real Valhalla /route success body so the Drive leg maps to a Measured result.
    private const string OkBody =
        "{\"trip\":{\"status\":0,\"summary\":{\"time\":1234.6,\"length\":56.789},\"legs\":[{\"shape\":\"_p~iF~ps|U_ulLnnqC\"}]}}";

    // --- The stub seam (mirrors ValhallaTravelTimeProviderTests): no real network. ---

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

    private static ValhallaTravelTimeProvider BuildValhalla(StubHandler handler, string baseUrl) =>
        new(
            new StubHttpClientFactory(handler),
            Options.Create(new ValhallaOptions { BaseUrl = baseUrl }),
            Options.Create(new TravelTimeOptions()),
            NullLogger<ValhallaTravelTimeProvider>.Instance);

    // ====================================================================================
    // AC-1a / NFR7 — the DEFAULT provider (smart-haversine) issues NO outbound HTTP.
    // ====================================================================================

    [Theory]
    [InlineData(TravelMode.Drive)]
    [InlineData(TravelMode.Walk)]
    [InlineData(TravelMode.Cycle)]
    public async Task DefaultProvider_GroundLeg_ComputesInProcess_WithNoHttpClientInPlay_Async(string mode)
    {
        // NFR7 (default rung): the Mock is built with ONLY Options — no IHttpClientFactory, no
        // HttpMessageHandler, no network anywhere in scope. A real Estimated result still comes
        // back, proving the leg is computed entirely in-process (no per-route egress is even possible).
        var provider = new MockTravelTimeProvider(Options.Create(new TravelTimeOptions()));

        var result = await provider.GetLegAsync(From, To, mode, CancellationToken.None);

        result.Fidelity.Should().Be(Fidelity.Estimated, "the smart-haversine default produces an Estimated ground leg");
        result.DistanceMeters.Should().BeGreaterThan(0, "a real great-circle distance is computed in-process");
        result.DurationSeconds.Should().BeGreaterThan(0, "a real duration is computed in-process");
        result.GeometryPolyline.Should().BeNull("the haversine default carries no road geometry");
    }

    [Fact]
    public void DefaultProvider_Ctor_HasNoHttpClientDependency_NFR7Structural()
    {
        // The structural NFR7 lock for the default rung: MockTravelTimeProvider's constructor takes
        // ONLY IOptions<TravelTimeOptions>. With no IHttpClientFactory/HttpClient parameter the
        // provider is physically incapable of an out-call — coordinates cannot egress. This guards
        // the "in-process" contract against a future regression that wires in an HttpClient.
        var ctor = typeof(MockTravelTimeProvider).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Should().ContainSingle("the default provider has a single public constructor").Subject;

        var parameterTypes = ctor.GetParameters().Select(p => p.ParameterType).ToArray();

        parameterTypes.Should().NotContain(typeof(IHttpClientFactory),
            "the default provider must never take an IHttpClientFactory — it computes in-process (NFR7)");
        parameterTypes.Should().NotContain(typeof(HttpClient),
            "the default provider must never take an HttpClient — it computes in-process (NFR7)");
        parameterTypes.Should().ContainSingle(t => t == typeof(IOptions<TravelTimeOptions>),
            "the default provider depends only on IOptions<TravelTimeOptions>");
    }

    // ====================================================================================
    // AC-1b / NFR7 — Valhalla contacts ONLY the one configured internal base-URL host.
    // ====================================================================================

    [Theory]
    [InlineData(TravelMode.Drive)]
    [InlineData(TravelMode.Walk)]
    [InlineData(TravelMode.Cycle)]
    public async Task Valhalla_GroundLeg_ContactsOnlyTheConfiguredHost_NoOtherEgress_Async(string mode)
    {
        // NFR7 (measured rung): a ground leg POSTs exactly ONCE, and the only host contacted is the
        // single configured internal BaseUrl — no Geofabrik, no public router, no second host. The
        // containment must hold for ALL THREE ground modes (AC-1 "across all three ground modes"):
        // each maps to a different Valhalla costing token but MUST still target the one configured
        // host (the existing GetLeg_Success_ReturnsMeasured_WithCostingToken asserts the costing
        // token only, not the host). The captured request URI's host AND port must equal the
        // configured one (mirrors GetLeg_TargetsConfiguredBaseUrl_WithTrailingSlashTrimmed).
        const string baseUrl = "http://valhalla.internal:9999";
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, OkBody));
        var provider = BuildValhalla(handler, baseUrl);

        var result = await provider.GetLegAsync(From, To, mode, CancellationToken.None);

        result.Fidelity.Should().Be(Fidelity.Measured, "a configured ground leg returns a Measured route");
        handler.CallCount.Should().Be(1, "exactly one /route POST per leg — no extra out-calls");

        var configured = new Uri(baseUrl);
        handler.LastRequestUri.Should().NotBeNull();
        handler.LastRequestUri!.Host.Should().Be(configured.Host,
            "the ONLY host contacted is the single configured internal base-URL host (NFR7 no-egress)");
        handler.LastRequestUri!.Port.Should().Be(configured.Port,
            "the configured port is honored — no traffic to any other endpoint");
        handler.LastRequestUri!.AbsolutePath.Should().Be("/route",
            "the single per-leg POST targets the one /route endpoint on the configured host");
    }

    [Fact]
    public async Task Valhalla_AirLeg_MakesNoHttpCall_NoEgress_Async()
    {
        // NFR7 (Air rung): an Air/AnyAir leg is a straight-line Placeholder computed before any HTTP.
        // CallCount == 0 proves no request — and therefore no coordinate — ever leaves the process
        // for an Air leg (mirrors GetLeg_AnyAir_MakesNoHttpCall_AndIsPlaceholder).
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, OkBody));
        var provider = BuildValhalla(handler, "http://valhalla.internal:9999");

        var result = await provider.GetLegAsync(From, To, TravelMode.AnyAir, CancellationToken.None);

        handler.CallCount.Should().Be(0, "an Air leg is never routed by Valhalla — no HTTP, no egress (NFR7)");
        result.Fidelity.Should().Be(Fidelity.Placeholder, "Air stays a straight-line Placeholder");
    }
}
