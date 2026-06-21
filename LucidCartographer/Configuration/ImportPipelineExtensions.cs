using Coravel;
using LucidCartographer.Services.Import;

namespace LucidCartographer.Configuration;

public static class ImportPipelineExtensions
{
    /// <summary>
    /// Registers file-import pipeline: stateless parsers (Singleton), orchestrator (Scoped),
    /// Coravel background queue (decoupled from Blazor circuit), and Google Maps scraper.
    /// </summary>
    public static IServiceCollection AddImportPipeline(this IServiceCollection services)
    {
        services.AddSingleton<IFileImporter, GpxImporter>();
        services.AddSingleton<IFileImporter, KmlImporter>();
        services.AddSingleton<IFileImporter, GeoJsonImporter>();
        services.AddSingleton<IFileImporter, CsvImporter>();
        services.AddScoped<IImportOrchestrator, ImportOrchestrator>();

        services.AddQueue();
        services.AddSingleton<ImportJobStatusService>();
        services.AddTransient<ImportInvocable>();
        services.AddSingleton<IImportJobQueue, CoravelImportJobQueue>();

        services.AddSingleton<IGoogleMapsListScraper, GoogleMapsListScraper>();
        return services;
    }
}
