using LucidCartographer.Services;
using LucidCartographer.Services.Operations;

namespace LucidCartographer.Configuration;

public static class DeduplicationServicesExtensions
{
    /// <summary>
    /// Registers the deduplication pipeline: SQLite write lock (shared with enrichment),
    /// wake trigger, dedup engine, and background service. Tunable via appsettings.json.
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
