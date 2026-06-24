using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using Microsoft.Extensions.Options;

namespace LucidCartographer.Services.Trip;

/// <summary>
/// Self-hosted Valhalla travel-time provider for Drive/Walk/Cycle legs. Issues a single
/// per-leg <c>/route</c> POST against one configured base URL (one engine, dynamic costing:
/// Drive→auto, Walk→pedestrian, Cycle→bicycle) and returns the measured road duration,
/// distance, and polyline6-encoded geometry. Any/Air legs return haversine-estimated
/// straight-line geometry without any HTTP. Valhalla errors degrade to Estimated via the
/// caller's catch. Directional: A→B is never mirrored to B→A.
/// </summary>
public sealed class ValhallaTravelTimeProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<ValhallaOptions> valhallaOptions,
    IOptions<TravelTimeOptions> travelTimeOptions,
    ILogger<ValhallaTravelTimeProvider> logger) : ITravelTimeProvider
{
    /// <summary>The named <see cref="IHttpClientFactory"/> client for Valhalla calls.</summary>
    public const string HttpClientName = "valhalla";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public string Source => TravelTimeSource.Valhalla;

    /// <summary>
    /// Valhalla routes over OpenStreetMap data, so ODbL attribution is required and surfaced
    /// on the map via UiStrings whenever Valhalla is the active provider.
    /// </summary>
    public string? Attribution => UiStrings.TripRoutingAttributionValhalla;

    /// <summary>Valhalla routes over the real road network, so its legs are measured.</summary>
    public bool ProducesMeasuredFidelity => true;

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

        var costing = ValhallaOptions.CostingFor(travelMode);
        if (costing is null)
        {
            throw new ValhallaRouteUnavailableException(
                $"Valhalla has no configured costing for travel mode '{travelMode}'.");
        }

        var baseUrl = valhallaOptions.Value.BaseUrl;
        var requestUri = $"{baseUrl.TrimEnd('/')}/route";
        var body = BuildRequestBody(from, to, costing);

        HttpResponseMessage response;
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            response = await client.PostAsync(requestUri, content, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // HttpClient timeout while our token is not cancelled: treat as unavailable route.
            throw new ValhallaRouteUnavailableException(
                $"Valhalla request to '{requestUri}' timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ValhallaRouteUnavailableException(
                $"Valhalla request to '{requestUri}' failed: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new ValhallaRouteUnavailableException(
                    $"Valhalla returned HTTP {(int)response.StatusCode} for '{requestUri}'.");
            }

            ValhallaRouteResponse? parsed;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                parsed = await JsonSerializer.DeserializeAsync<ValhallaRouteResponse>(stream, JsonOptions, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (JsonException ex)
            {
                throw new ValhallaRouteUnavailableException(
                    $"Valhalla returned an unparseable response for '{requestUri}'.", ex);
            }

            return MapResponse(parsed, requestUri);
        }
    }

    /// <summary>
    /// Builds the Valhalla <c>/route</c> POST body. Valhalla expects <c>{lat, lon}</c> order
    /// (opposite of OSRM's lon,lat); a typed record serialized by System.Text.Json writes the
    /// <c>double</c> coordinates invariant-culture by default, sidestepping comma-decimal corruption.
    /// </summary>
    private static string BuildRequestBody(TravelEndpoint from, TravelEndpoint to, string costing)
    {
        var request = new ValhallaRouteRequest
        {
            Locations =
            [
                new ValhallaLocation { Lat = from.Latitude, Lon = from.Longitude },
                new ValhallaLocation { Lat = to.Latitude, Lon = to.Longitude },
            ],
            Costing = costing,
            DirectionsOptions = new ValhallaDirectionsOptions { Units = "kilometers" },
        };

        return JsonSerializer.Serialize(request, JsonOptions);
    }

    private TravelLegResult MapResponse(ValhallaRouteResponse? parsed, string requestUri)
    {
        var trip = parsed?.Trip;
        if (trip is null || trip.Status != 0)
        {
            var status = trip is null ? "(none)" : trip.Status.ToString(CultureInfo.InvariantCulture);
            throw new ValhallaRouteUnavailableException(
                $"Valhalla returned trip status '{status}' for '{requestUri}'.");
        }

        if (trip.Summary is null)
        {
            throw new ValhallaRouteUnavailableException(
                $"Valhalla returned no trip summary for '{requestUri}'.");
        }

        if (trip.Legs is not { Count: > 0 } legs)
        {
            throw new ValhallaRouteUnavailableException(
                $"Valhalla returned an empty leg set for '{requestUri}'.");
        }

        var geometry = legs[0].Shape;

        // Measured legs MUST carry geometry — a null geometry would persist unchecked since Upsert protects Measured rows.
        if (string.IsNullOrWhiteSpace(geometry))
        {
            throw new ValhallaRouteUnavailableException(
                $"Valhalla returned a route with no geometry for '{requestUri}'.");
        }

        // Edge conversions only (NFR-11): seconds rounded to int; length km → ×1000 meters.
        var seconds = (int)Math.Round(trip.Summary.Time);
        var meters = trip.Summary.Length * 1000.0;

        logger.LogDebug(
            "Measured leg via Valhalla — {Seconds}s / {Meters}m (geometry {HasGeometry})",
            seconds, meters, "present");

        return new TravelLegResult(seconds, meters, Fidelity.Measured, geometry);
    }

    private sealed class ValhallaRouteRequest
    {
        [JsonPropertyName("locations")]
        public List<ValhallaLocation> Locations { get; set; } = [];

        [JsonPropertyName("costing")]
        public string Costing { get; set; } = string.Empty;

        [JsonPropertyName("directions_options")]
        public ValhallaDirectionsOptions DirectionsOptions { get; set; } = new();
    }

    private sealed class ValhallaLocation
    {
        [JsonPropertyName("lat")]
        public double Lat { get; set; }

        [JsonPropertyName("lon")]
        public double Lon { get; set; }
    }

    private sealed class ValhallaDirectionsOptions
    {
        [JsonPropertyName("units")]
        public string Units { get; set; } = "kilometers";
    }

    private sealed class ValhallaRouteResponse
    {
        [JsonPropertyName("trip")]
        public ValhallaTrip? Trip { get; set; }
    }

    private sealed class ValhallaTrip
    {
        [JsonPropertyName("status")]
        public int Status { get; set; }

        [JsonPropertyName("summary")]
        public ValhallaSummary? Summary { get; set; }

        [JsonPropertyName("legs")]
        public List<ValhallaLeg>? Legs { get; set; }
    }

    private sealed class ValhallaSummary
    {
        [JsonPropertyName("time")]
        public double Time { get; set; }

        [JsonPropertyName("length")]
        public double Length { get; set; }
    }

    private sealed class ValhallaLeg
    {
        [JsonPropertyName("shape")]
        public string? Shape { get; set; }
    }
}
