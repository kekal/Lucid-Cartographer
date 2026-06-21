using LucidCartographer.Services.Enrichment;

namespace LucidCartographer.Configuration;

public static class EnrichmentPipelineExtensions
{
    /// <summary>
    /// Registers background enrichment service for scraping place details.
    /// Configurable via appsettings.json "Enrichment" section.
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
