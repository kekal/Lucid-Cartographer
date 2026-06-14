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
        services.AddScoped<ITripOrderingService, TripOrderingService>();

        // Singletons shared between the VM and the hosted compute loop. Safe to
        // register without the provider/hosted service: the VM only signals the
        // trigger and reads the progress count — nothing here resolves the Polly
        // pipeline, so a host that omits the compute service still boots.
        services.AddSingleton<TravelTimeTrigger>();
        services.AddSingleton<TravelTimeProgressService>();
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

        // Exactly one active provider — the haversine Mock by default. The hosted
        // service does the off-circuit compute and is the only consumer of both the
        // provider and the "travel-time" resilience pipeline.
        services.AddSingleton<ITravelTimeProvider, MockTravelTimeProvider>();
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
