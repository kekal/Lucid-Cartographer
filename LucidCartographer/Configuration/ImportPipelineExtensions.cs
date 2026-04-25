using Coravel;
using LucidCartographer.Services.Import;

namespace LucidCartographer.Configuration;

public static class ImportPipelineExtensions
{
    /// <summary>
    /// Registers the file-import pipeline:
    ///   - Stateless parsers (Singleton) — ARCH-HIGH-02
    ///   - Per-request orchestrator (Scoped)
    ///   - Background queue via Coravel — user clicks Import → job is enqueued
    ///     via IImportJobQueue → Coravel's scheduler runs it on a background
    ///     thread inside its own DI scope, decoupled from the Blazor circuit.
    ///     The user is free to navigate away; ImportJobStatusService publishes
    ///     lifecycle events the UI subscribes to.
    ///   - Google Maps list scraper (Singleton with internal SemaphoreSlim,
    ///     HIGH-07).
    /// </summary>
    public static IServiceCollection AddImportPipeline(this IServiceCollection services)
    {
        services.AddSingleton<IFileImporter, GpxImporter>();
        services.AddSingleton<IFileImporter, KmlImporter>();
        services.AddSingleton<IFileImporter, GeoJsonImporter>();
        services.AddSingleton<IFileImporter, CsvImporter>();
        services.AddScoped<IImportOrchestrator, ImportOrchestrator>();

        // Coravel-backed background import pipeline
        services.AddQueue();
        services.AddSingleton<ImportJobStatusService>();
        services.AddTransient<ImportInvocable>();
        services.AddSingleton<IImportJobQueue, CoravelImportJobQueue>();

        services.AddSingleton<IGoogleMapsListScraper, GoogleMapsListScraper>();
        return services;
    }
}
