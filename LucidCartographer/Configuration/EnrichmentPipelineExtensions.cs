using LucidCartographer.Services.Enrichment;

namespace LucidCartographer.Configuration;

public static class EnrichmentPipelineExtensions
{
    /// <summary>
    /// Background enrichment: fills address/website/phone for Google-scraped Pois
    /// by opening each place URL in a headless tab. Runs continuously, polling the
    /// DB for IsEnriched=false rows. Progress service is a singleton the MapPage
    /// subscribes to for its "N pending" counter.
    /// Tunable via the "Enrichment" section of appsettings.json — Concurrency,
    /// BatchSize, IdlePollSeconds.
    /// </summary>
    public static IServiceCollection AddEnrichmentPipeline(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<EnrichmentProgressService>();
        services.AddSingleton<EnrichmentTrigger>();
        services.AddHttpClient();
        services.Configure<EnrichmentOptions>(configuration.GetSection("Enrichment"));
        services.AddHostedService<PoiEnrichmentBackgroundService>();
        return services;
    }
}
