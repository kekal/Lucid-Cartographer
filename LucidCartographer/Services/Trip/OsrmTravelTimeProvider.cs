using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LucidCartographer.Data.Entities;
using Microsoft.Extensions.Options;

namespace LucidCartographer.Services.Trip;

/// <summary>
/// TRIP-OSRM-01 (Story 4.1): the optional, opt-in self-hosted OSRM travel-time
/// provider (AR-3). For a Drive/Walk/Cycle leg it issues a single per-leg OSRM
/// <c>/route</c> query against the per-profile backend (Drive→car, Walk→foot,
/// Cycle→bike) and returns a <see cref="TravelLegResult"/> with
/// <see cref="Fidelity.Measured"/>, the measured road duration (seconds) and
/// distance (meters), and the encoded-polyline road geometry.
///
/// Design contract (see story Dev Notes):
/// <list type="bullet">
/// <item><b>Encoded polyline</b> (<c>geometries=polyline</c>, precision 5) stored
/// verbatim in <see cref="RouteSegment.GeometryPolyline"/> — a deliberate,
/// documented deviation from AR-3's literal <c>geojson</c> (same geometry, more
/// compact, decodes natively in Leaflet for Story 4.2).</item>
/// <item><b>/route per leg only</b> — <c>/table</c> is out of scope; the matrix is
/// built from the shared cache, not from a provider call.</item>
/// <item><b>Directional</b> ([TRIP-CACHE-01], A9): each A→B leg is its own query;
/// A→B is never mirrored onto B→A (OSRM Drive routes can be genuinely asymmetric
/// because of one-way streets).</item>
/// <item><b>Degrade-by-throwing</b> (AC3): no-route / unreachable / timeout /
/// HTTP-error / unconfigured-profile ⇒ THROW so the existing background-service
/// catch (TRIP-DEGRADE-01) substitutes the haversine Estimated value. No second
/// fallback lives here. A real cancellation re-throws
/// <see cref="OperationCanceledException"/>.</item>
/// <item><b>Any/Air</b> ⇒ no HTTP at all; returns a straight-line
/// <see cref="Fidelity.Placeholder"/> result, identical to
/// <see cref="MockTravelTimeProvider"/> (FR-8, AR-10).</item>
/// </list>
/// NFR7: OSRM is self-hosted so no coordinate leaves the deployment — no egress,
/// no consent guard required.
/// </summary>
public sealed class OsrmTravelTimeProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<OsrmOptions> osrmOptions,
    IOptions<TravelTimeOptions> travelTimeOptions,
    ILogger<OsrmTravelTimeProvider> logger) : ITravelTimeProvider
{
    /// <summary>The named <see cref="IHttpClientFactory"/> client for OSRM calls.</summary>
    public const string HttpClientName = "osrm";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public string Source => TravelTimeSource.Osrm;

    public async Task<TravelLegResult> GetLegAsync(
        TravelEndpoint from,
        TravelEndpoint to,
        string travelMode,
        CancellationToken ct)
    {
        // AC2 (FR-8, AR-10): Any/Air is NEVER routed by OSRM — no HTTP call. Mirror
        // MockTravelTimeProvider: a straight-line estimate re-badged Placeholder,
        // carrying no road geometry (Air has none).
        if (string.Equals(travelMode, TravelMode.AnyAir, StringComparison.Ordinal))
        {
            var estimate = EstimatedTravelTime.Compute(from, to, travelMode, travelTimeOptions.Value);
            return estimate with { Fidelity = Fidelity.Placeholder };
        }

        // Drive/Walk/Cycle: resolve the OSRM profile + its per-profile base URL.
        var profile = OsrmOptions.ProfileFor(travelMode);
        var baseUrl = osrmOptions.Value.BaseUrlFor(travelMode);
        if (profile is null || baseUrl is null)
        {
            // No configured coverage for this mode ⇒ throw so the loop degrades (AC3).
            throw new OsrmRouteUnavailableException(
                $"OSRM has no configured base URL for travel mode '{travelMode}'.");
        }

        var requestUri = BuildRouteUri(baseUrl, profile, from, to, osrmOptions.Value.GeometryPrecision);

        HttpResponseMessage response;
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            response = await client.GetAsync(requestUri, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Real cancellation — never swallow it (the caller is shutting down).
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // Timeout (HttpClient cancels via a linked token when its Timeout
            // elapses) while OUR token is NOT cancelled ⇒ treat as "no usable
            // route" and degrade (AC3).
            throw new OsrmRouteUnavailableException(
                $"OSRM request to '{requestUri}' timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            // Unreachable host / connection error ⇒ degrade (AC3).
            throw new OsrmRouteUnavailableException(
                $"OSRM request to '{requestUri}' failed: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new OsrmRouteUnavailableException(
                    $"OSRM returned HTTP {(int)response.StatusCode} for '{requestUri}'.");
            }

            OsrmRouteResponse? parsed;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                parsed = await JsonSerializer.DeserializeAsync<OsrmRouteResponse>(stream, JsonOptions, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (JsonException ex)
            {
                throw new OsrmRouteUnavailableException(
                    $"OSRM returned an unparseable response for '{requestUri}'.", ex);
            }

            return MapResponse(parsed, requestUri);
        }
    }

    /// <summary>
    /// Builds the OSRM <c>/route</c> request URI. NOTE: OSRM coordinate order is
    /// <c>lon,lat</c> (not lat,lon). Coordinates are formatted with the invariant
    /// culture so a comma decimal separator can never corrupt the query.
    /// </summary>
    private static string BuildRouteUri(
        string baseUrl, string profile, TravelEndpoint from, TravelEndpoint to, int precision)
    {
        var trimmedBase = baseUrl.TrimEnd('/');
        var coordinates = string.Format(
            CultureInfo.InvariantCulture,
            "{0},{1};{2},{3}",
            from.Longitude, from.Latitude, to.Longitude, to.Latitude);

        // overview=full + geometries=polyline ⇒ a single encoded-polyline string
        // (precision 5 = "polyline", precision 6 = "polyline6"). alternatives/steps
        // off — we only need duration, distance and the route geometry.
        var geometries = precision == 6 ? "polyline6" : "polyline";
        return $"{trimmedBase}/route/v1/{profile}/{coordinates}?overview=full&geometries={geometries}&alternatives=false&steps=false";
    }

    private TravelLegResult MapResponse(OsrmRouteResponse? parsed, string requestUri)
    {
        // require code == "Ok" and a non-empty routes[0] (AC1/AC3).
        if (parsed is null || !string.Equals(parsed.Code, "Ok", StringComparison.Ordinal))
        {
            var code = parsed?.Code ?? "(none)";
            throw new OsrmRouteUnavailableException(
                $"OSRM returned code '{code}' for '{requestUri}'.");
        }

        if (parsed.Routes is not { Count: > 0 } routes)
        {
            throw new OsrmRouteUnavailableException(
                $"OSRM returned an empty route set for '{requestUri}'.");
        }

        var route = routes[0];

        // A Measured leg MUST carry road geometry (AC1) — that is the whole point of
        // routing over OSRM and Story 4.2 reads it to draw the solid road line. A
        // "code":"Ok" response with no geometry (a mis-configured backend, or a body
        // that omitted it) would otherwise persist a geometry-less Measured row that
        // never re-invalidates (Upsert protects Measured), permanently starving 4.2.
        // Treat it as "no usable route" and throw so the leg degrades to Estimated (AC3).
        if (string.IsNullOrWhiteSpace(route.Geometry))
        {
            throw new OsrmRouteUnavailableException(
                $"OSRM returned a route with no geometry for '{requestUri}'.");
        }

        // Canonical units at the edge (AR-11): OSRM already returns seconds/meters;
        // round duration to an int. Geometry is the encoded-polyline string stored
        // verbatim in RouteSegment.GeometryPolyline (TRIP-OSRM-01).
        var seconds = (int)Math.Round(route.Duration);
        var meters = route.Distance;
        var geometry = route.Geometry;

        logger.LogDebug(
            "TRIP-OSRM-01: measured leg via OSRM — {Seconds}s / {Meters}m (geometry {HasGeometry})",
            seconds, meters, geometry is null ? "absent" : "present");

        return new TravelLegResult(seconds, meters, Fidelity.Measured, geometry);
    }

    // --- OSRM /route response DTOs (System.Text.Json) ---

    private sealed class OsrmRouteResponse
    {
        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("routes")]
        public List<OsrmRoute>? Routes { get; set; }
    }

    private sealed class OsrmRoute
    {
        [JsonPropertyName("duration")]
        public double Duration { get; set; }

        [JsonPropertyName("distance")]
        public double Distance { get; set; }

        [JsonPropertyName("geometry")]
        public string? Geometry { get; set; }
    }
}
