using LucidCartographer.Services;
using LucidCartographer.Services.Trip;

namespace LucidCartographer.Configuration;

public static class TripServicesExtensions
{
    /// <summary>
    /// VM-facing services: ordering, cache invalidation, travel-time signals, and a mock provider.
    /// Excludes the hosted compute service and active provider so tests can compose in isolation.
    /// </summary>
    public static IServiceCollection AddTripServices(this IServiceCollection services)
    {
        services.TryAddSingletonWriteLock();

        // Distance Matrix must register here: it is a dependency of TripOrderingService (TSP-Sort)
        // and reads only the RouteSegment cache; integration tests resolve it via this overload.
        services.AddScoped<IDistanceMatrixService, DistanceMatrixService>();
        services.AddScoped<ITripOrderingService, TripOrderingService>();

        // Cache invalidation service: deletes stale RouteSegments under SqliteWriteLock;
        // background compute refills them on next trigger.
        services.AddScoped<IRouteSegmentInvalidationService, RouteSegmentInvalidationService>();

        // Shared signals between VM and compute loop; safe to register without hosted service.
        services.AddSingleton<TravelTimeTrigger>();
        services.AddSingleton<TravelTimeProgressService>();

        // Mock provider (haversine): default for tests. Has no Attribution and no external deps.
        // Production overload re-registers after this, so config-selected provider (e.g., OSRM) wins.
        services.AddSingleton<ITravelTimeProvider, MockTravelTimeProvider>();
        return services;
    }

    /// <summary>
    /// Full production wiring: adds VM-facing services plus hosted compute service and Polly pipeline.
    /// </summary>
    public static IServiceCollection AddTripServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddTripServices();

        // Select provider by config: default is Mock (haversine), only "Osrm" swaps in OSRM provider.
        // OSRM is opt-in, never the default. Hosted service consumes provider + pipeline.
        var providerId = configuration["TravelTime:Provider"];
        if (string.Equals(providerId, "Osrm", StringComparison.OrdinalIgnoreCase))
        {
            // Bind OSRM options and register named HttpClient "osrm" with User-Agent.
            // Self-hosted, so no egress guard needed.
            services.Configure<OsrmOptions>(configuration.GetSection("TravelTime:Osrm"));

            var timeoutSeconds = configuration.GetValue<int?>("TravelTime:Osrm:RequestTimeoutSeconds") ?? 10;
            services.AddHttpClient(OsrmTravelTimeProvider.HttpClientName, c =>
            {
                c.Timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds));
                c.DefaultRequestHeaders.UserAgent.ParseAdd("LucidCartographer/1.0 (+osrm-routing)");
            });

            services.AddSingleton<ITravelTimeProvider, OsrmTravelTimeProvider>();
        }
        else
        {
            services.AddSingleton<ITravelTimeProvider, MockTravelTimeProvider>();
        }

        services.AddHostedService<TravelTimeComputationBackgroundService>();
        services.Configure<TravelTimeOptions>(configuration.GetSection("TravelTime"));
        return services;
    }

    private static void TryAddSingletonWriteLock(this IServiceCollection services)
    {
        // Reuse SqliteWriteLock if already registered (shared with enrichment/dedup);
        // only register here if not present, so this slice can work in isolation.
        if (services.All(d => d.ServiceType != typeof(SqliteWriteLock)))
        {
            services.AddSingleton<SqliteWriteLock>();
        }
    }
}
