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
        // Production overload re-registers after this, so config-selected provider (e.g., Valhalla) wins.
        services.AddSingleton<ITravelTimeProvider, MockTravelTimeProvider>();
        return services;
    }

    /// <summary>
    /// Classifies a configured <c>TravelTime:Provider</c> value into the active selection.
    /// </summary>
    public enum ProviderSelection
    {
        /// <summary>Empty/missing or the explicit "Mock" default → smart-haversine.</summary>
        Default,

        /// <summary>The measured self-hosted Valhalla provider.</summary>
        Valhalla,

        /// <summary>A retired ("Osrm") or otherwise unrecognized id → falls back to the default with a warning.</summary>
        RetiredOrUnknown,
    }

    /// <summary>
    /// Pure, testable classification of the configured provider id. Empty/missing and "Mock"
    /// resolve to <see cref="ProviderSelection.Default"/>; "Valhalla" to that provider; anything
    /// else (including the retired "Osrm") to <see cref="ProviderSelection.RetiredOrUnknown"/>,
    /// which the caller surfaces as a prominent warn-and-fall-back (FR-15, AD-7 — never fail-fast).
    /// </summary>
    public static ProviderSelection ClassifyProvider(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)
            || string.Equals(providerId, "Mock", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderSelection.Default;
        }

        if (string.Equals(providerId, "Valhalla", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderSelection.Valhalla;
        }

        return ProviderSelection.RetiredOrUnknown;
    }

    /// <summary>
    /// Full production wiring: adds VM-facing services plus hosted compute service and Polly pipeline.
    /// </summary>
    public static IServiceCollection AddTripServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddTripServices();

        // Select provider by config: default is Mock (smart-haversine), only "Valhalla"
        // swaps in the measured Valhalla provider. Valhalla is opt-in, never the default.
        // A retired ("Osrm") or unknown id never bricks boot — it warns prominently and
        // falls back to the default (FR-15, AD-7).
        var providerId = configuration["TravelTime:Provider"];
        switch (ClassifyProvider(providerId))
        {
            case ProviderSelection.Valhalla:
                // Bind Valhalla options and register the named "valhalla" HttpClient.
                // Self-hosted (coordinates never egress), so no egress guard needed.
                services.Configure<ValhallaOptions>(configuration.GetSection("TravelTime:Valhalla"));

                var timeoutSeconds = configuration.GetValue<int?>("TravelTime:Valhalla:RequestTimeoutSeconds") ?? 10;
                services.AddHttpClient(ValhallaTravelTimeProvider.HttpClientName, c =>
                {
                    c.Timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds));
                });

                services.AddSingleton<ITravelTimeProvider, ValhallaTravelTimeProvider>();
                break;

            case ProviderSelection.RetiredOrUnknown:
                // Loud, high-level startup warning: this deployment is silently downgraded
                // from Measured to Estimated. Selection runs before the app's ILogger exists,
                // so emit via a one-off bootstrap logger, then register the default.
                WarnRetiredProvider(providerId);
                services.AddSingleton<ITravelTimeProvider, MockTravelTimeProvider>();
                break;

            default:
                services.AddSingleton<ITravelTimeProvider, MockTravelTimeProvider>();
                break;
        }

        services.AddHostedService<TravelTimeComputationBackgroundService>();
        services.Configure<TravelTimeOptions>(configuration.GetSection("TravelTime"));
        return services;
    }

    /// <summary>
    /// Emits the prominent retired/unknown-provider warning. Uses a self-contained bootstrap
    /// logger because provider selection happens during service registration, before the host's
    /// logging pipeline is available.
    /// </summary>
    private static void WarnRetiredProvider(string? providerId)
    {
        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var logger = loggerFactory.CreateLogger("LucidCartographer.Configuration.TripServices");
        logger.LogWarning(
            "TravelTime:Provider is set to '{ProviderId}', which is not a recognized provider " +
            "(the OSRM provider has been retired). Falling back to the default smart-haversine " +
            "estimate — routing is now ESTIMATED, not MEASURED. Set TravelTime:Provider=Valhalla " +
            "(and start the 'valhalla' compose profile) to restore measured road times.",
            providerId);
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
