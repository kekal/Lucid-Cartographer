using LucidCartographer.Services.Export;

namespace LucidCartographer.Configuration;

public static class ExportPipelineExtensions
{
    /// <summary>
    /// File exporters (Singleton) plus the Google Maps Saved-List export pipeline:
    /// the headful exporter and a dedicated, cancellable background job. The job
    /// runs on its OWN single-consumer channel (<see cref="ExportJobQueue"/> +
    /// <see cref="ExportBackgroundService"/>), NOT the shared Coravel import queue,
    /// so a tens-of-minutes headful run never blocks imports or graceful shutdown.
    /// </summary>
    public static IServiceCollection AddExportPipeline(this IServiceCollection services)
    {
        services.AddSingleton<IFileExporter, KmlExporter>();
        services.AddSingleton<IFileExporter, GpxExporter>();

        // GoogleBrowserLock + the shared IBrowserSession are registered by
        // AddBrowserSession (called from Program.cs) — the exporter injects both.

        // Headful Google Maps Saved-List exporter (single browser session).
        services.AddSingleton<IGoogleMapsListExporter, GoogleMapsListExporter>();

        // Dedicated cancellable background export pipeline (Channel + hosted consumer).
        services.AddSingleton<ExportJobStatusService>();
        services.AddSingleton<ExportJobQueue>();
        services.AddSingleton<IExportJobQueue>(sp => sp.GetRequiredService<ExportJobQueue>());
        services.AddScoped<ExportJobProcessor>();
        services.AddHostedService<ExportBackgroundService>();

        return services;
    }
}
