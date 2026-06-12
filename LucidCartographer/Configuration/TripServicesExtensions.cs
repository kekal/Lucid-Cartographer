using LucidCartographer.Services;
using LucidCartographer.Services.Trip;

namespace LucidCartographer.Configuration;

public static class TripServicesExtensions
{
    /// <summary>
    /// Trip View slice. Registers the ordering service (Scoped, matching the
    /// per-slice precedent of <c>IPoiDeduplicationService</c>) which owns the
    /// single <c>OrderIndex</c> write-path. The shared <see cref="SqliteWriteLock"/>
    /// is a singleton owned by <c>AddDeduplicationPipeline</c> — reuse it,
    /// registering a fallback only if no pipeline registered one first so the
    /// slice stays self-contained for tests.
    /// </summary>
    public static IServiceCollection AddTripServices(this IServiceCollection services)
    {
        services.TryAddSingletonWriteLock();
        services.AddScoped<ITripOrderingService, TripOrderingService>();
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
