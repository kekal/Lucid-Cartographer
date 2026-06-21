using LucidCartographer.Services.Export;

namespace LucidCartographer.Configuration;

public static class ExportPipelineExtensions
{
    /// <summary>
    /// File exporters and Google Maps export pipeline. Exports run on a dedicated channel,
    /// not the shared import queue, so they never block imports or graceful shutdown.
    /// </summary>
    public static IServiceCollection AddExportPipeline(this IServiceCollection services)
    {
        services.AddSingleton<IFileExporter, KmlExporter>();
        services.AddSingleton<IFileExporter, GpxExporter>();

        // GoogleBrowserLock + IBrowserSession are registered by AddBrowserSession.
        services.AddSingleton<IGoogleMapsListExporter, GoogleMapsListExporter>();

        services.AddSingleton<ExportJobStatusService>();
        services.AddSingleton<ExportJobQueue>();
        services.AddSingleton<IExportJobQueue>(sp => sp.GetRequiredService<ExportJobQueue>());
        services.AddScoped<ExportJobProcessor>();
        services.AddHostedService<ExportBackgroundService>();

        return services;
    }
}
