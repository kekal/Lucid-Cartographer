using LucidCartographer.Services;
using LucidCartographer.Services.Operations;

namespace LucidCartographer.Configuration;

public static class DeduplicationServicesExtensions
{
    /// <summary>
    /// Whole-database deduplication pipeline. Registers the shared SQLite
    /// write lock (also used by the enrichment worker), the wake trigger, the
    /// scoped dedup engine, and the background service that runs a pass on
    /// enrichment-drain signals and once per configured interval.
    /// Tunable via the "Deduplication" section of appsettings.json.
    /// </summary>
    public static IServiceCollection AddDeduplicationPipeline(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<SqliteWriteLock>();
        services.AddSingleton<DedupTrigger>();
        services.AddScoped<IPoiDeduplicationService, PoiDeduplicationService>();
        services.Configure<DeduplicationOptions>(configuration.GetSection("Deduplication"));
        services.AddHostedService<PoiDeduplicationBackgroundService>();
        return services;
    }
}
