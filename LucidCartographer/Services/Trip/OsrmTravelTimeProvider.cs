using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using Microsoft.Extensions.Options;

namespace LucidCartographer.Services.Trip;

/// <summary>
/// Self-hosted OSRM travel-time provider for Drive/Walk/Cycle legs. Issues a single
/// per-leg <c>/route</c> query against the per-profile backend and returns the
/// measured road duration, distance, and encoded-polyline geometry. Any/Air legs
/// return haversine-estimated straight-line geometry without HTTP. OSRM errors degrade
/// to Estimated via the caller's catch. Directional: A→B is never mirrored to B→A.
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

    /// <summary>
    /// OSRM routes over OpenStreetMap data, so ODbL attribution is required and surfaced
    /// on the map via UiStrings whenever OSRM is the active provider.
    /// </summary>
    public string? Attribution => UiStrings.TripRoutingAttributionOsm;

    public async Task<TravelLegResult> GetLegAsync(
        TravelEndpoint from,
        TravelEndpoint to,
        string travelMode,
        CancellationToken ct)
    {
        // Any/Air: return haversine-estimated straight-line (no HTTP call, no geometry).
        if (string.Equals(travelMode, TravelMode.AnyAir, StringComparison.Ordinal))
        {
            var estimate = EstimatedTravelTime.Compute(from, to, travelMode, travelTimeOptions.Value);
            return estimate with { Fidelity = Fidelity.Placeholder };
        }

        var profile = OsrmOptions.ProfileFor(travelMode);
        var baseUrl = osrmOptions.Value.BaseUrlFor(travelMode);
        if (profile is null || baseUrl is null)
        {
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
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // HttpClient timeout while our token is not cancelled: treat as unavailable route.
            throw new OsrmRouteUnavailableException(
                $"OSRM request to '{requestUri}' timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
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
    /// Builds the OSRM <c>/route</c> request URI. OSRM expects lon,lat order
    /// (not lat,lon); coordinates use invariant culture to prevent comma-decimal corruption.
    /// </summary>
    private static string BuildRouteUri(
        string baseUrl, string profile, TravelEndpoint from, TravelEndpoint to, int precision)
    {
        var trimmedBase = baseUrl.TrimEnd('/');
        var coordinates = string.Format(
            CultureInfo.InvariantCulture,
            "{0},{1};{2},{3}",
            from.Longitude, from.Latitude, to.Longitude, to.Latitude);

        var geometries = precision == 6 ? "polyline6" : "polyline";
        return $"{trimmedBase}/route/v1/{profile}/{coordinates}?overview=full&geometries={geometries}&alternatives=false&steps=false";
    }

    private TravelLegResult MapResponse(OsrmRouteResponse? parsed, string requestUri)
    {
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

        // Measured legs MUST carry geometry — a null geometry would persist unchecked since Upsert protects Measured rows.
        if (string.IsNullOrWhiteSpace(route.Geometry))
        {
            throw new OsrmRouteUnavailableException(
                $"OSRM returned a route with no geometry for '{requestUri}'.");
        }

        var seconds = (int)Math.Round(route.Duration);
        var meters = route.Distance;
        var geometry = route.Geometry;

        logger.LogDebug(
            "Measured leg via OSRM — {Seconds}s / {Meters}m (geometry {HasGeometry})",
            seconds, meters, geometry is null ? "absent" : "present");

        return new TravelLegResult(seconds, meters, Fidelity.Measured, geometry);
    }

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
