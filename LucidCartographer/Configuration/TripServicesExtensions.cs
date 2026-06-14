using LucidCartographer.Services;
using LucidCartographer.Services.Trip;

namespace LucidCartographer.Configuration;

public static class TripServicesExtensions
{
    /// <summary>
    /// Trip View slice — the VM-facing services only. Registers the ordering
    /// service (Scoped, matching the per-slice precedent of
    /// <c>IPoiDeduplicationService</c>) which owns the single <c>OrderIndex</c>
    /// write-path, plus the travel-time <see cref="TravelTimeTrigger"/> and
    /// <see cref="TravelTimeProgressService"/> singletons that <c>TripViewModel</c>
    /// signals/reads. The shared <see cref="SqliteWriteLock"/> is a singleton owned
    /// by <c>AddDeduplicationPipeline</c> — reuse it, registering a fallback only if
    /// no pipeline registered one first so the slice stays self-contained for tests.
    ///
    /// TRIP-TRAVELTIME-01: this overload deliberately does NOT register the active
    /// provider or the off-circuit hosted compute service. Those resolve the Polly
    /// "travel-time" pipeline and self-fire a background loop, so — exactly like the
    /// enrichment/dedup hosted passes — they belong only in the production wiring
    /// (the <see cref="IConfiguration"/> overload). The integration test host
    /// composes services by hand and calls this parameterless overload, so it gets a
    /// renderable VM with no self-firing loop and no resilience-pipeline dependency.
    /// </summary>
    public static IServiceCollection AddTripServices(this IServiceCollection services)
    {
        services.TryAddSingletonWriteLock();

        // TRIP-MATRIX-01 (Story 3.1): the on-demand Distance Matrix is a dependency
        // of TripOrderingService (TSP-Sort), so it MUST register in this
        // parameterless overload — the integration host composes services by hand
        // and resolves TripOrderingService here; omitting the matrix would fail its
        // construction. It reads the shared RouteSegment cache + bound
        // TravelTimeOptions (IOptions<> resolves to defaults when unconfigured) and
        // writes nothing, so it carries no provider / Polly / hosted dependency.
        services.AddScoped<IDistanceMatrixService, DistanceMatrixService>();
        services.AddScoped<ITripOrderingService, TripOrderingService>();

        // TRIP-INVALIDATE-01 (Story 2.4): the cache-invalidation service is VM- and
        // edit-path facing (no provider / Polly / hosted dependency), so it lives in
        // this parameterless overload — the integration host that composes services
        // by hand can resolve it, and the production overload inherits it. It deletes
        // stale RouteSegment rows under the shared SqliteWriteLock; the existing
        // background compute refills them on the next trigger.
        services.AddScoped<IRouteSegmentInvalidationService, RouteSegmentInvalidationService>();

        // Singletons shared between the VM and the hosted compute loop. Safe to
        // register without the provider/hosted service: the VM only signals the
        // trigger and reads the progress count — nothing here resolves the Polly
        // pipeline, so a host that omits the compute service still boots.
        services.AddSingleton<TravelTimeTrigger>();
        services.AddSingleton<TravelTimeProgressService>();

        // TRIP-OSRM-02 (Story 4.2): TripViewModel now reads the active provider's
        // routing Attribution (AC4), so a provider MUST be resolvable in this
        // parameterless overload — the integration host composes by hand and calls it,
        // and without a provider the VM ctor would fail to construct (A3 gate). The
        // haversine Mock is the only provider with no Polly/hosted/HTTP dependency, and
        // it declares a null Attribution (haversine isn't OSM-derived), so it is the
        // correct default here: no routing attribution under the default wiring. The
        // production IConfiguration overload re-registers the config-selected provider
        // AFTER calling this overload, and the last ITravelTimeProvider registration
        // wins on resolve — so "Osrm" still swaps in cleanly in production.
        services.AddSingleton<ITravelTimeProvider, MockTravelTimeProvider>();
        return services;
    }

    /// <summary>
    /// TRIP-TRAVELTIME-01: full production Trip wiring. Adds the VM-facing services
    /// (parameterless overload) plus the off-circuit travel-time compute slice — the
    /// active provider (haversine Mock), the bound <see cref="TravelTimeOptions"/>,
    /// and the hosted <c>TravelTimeComputationBackgroundService</c> (AR-5) that
    /// resolves the Polly "travel-time" pipeline. <c>Program.cs</c> calls this
    /// overload; tests call the parameterless one.
    /// </summary>
    public static IServiceCollection AddTripServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddTripServices();

        // TRIP-OSRM-01 (Story 4.1): exactly one active provider, selected by config.
        // The DEFAULT (missing / "Mock" / anything unrecognized) stays the haversine
        // Mock (NFR9 — OSRM is opt-in, never the default). Only "Osrm" swaps in the
        // self-hosted OSRM provider. The hosted service is the sole consumer of both
        // the provider and the "travel-time" resilience pipeline.
        var providerId = configuration["TravelTime:Provider"];
        if (string.Equals(providerId, "Osrm", StringComparison.OrdinalIgnoreCase))
        {
            // Bind the per-profile OSRM options and register a named IHttpClientFactory
            // client "osrm" (timeout from options, a LucidCartographer User-Agent),
            // mirroring the PoiServicesExtensions named-client pattern. NFR7: OSRM is
            // self-hosted, so these calls never leave the deployment — no egress guard.
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
        // SqliteWriteLock is the process-wide write gate shared with the
        // enrichment worker and dedup pass. Reuse the singleton if one is
        // already registered; only add it here when nothing else has, so this
        // slice can be registered in isolation without a second gate instance.
        if (services.All(d => d.ServiceType != typeof(SqliteWriteLock)))
        {
            services.AddSingleton<SqliteWriteLock>();
        }
    }
}
