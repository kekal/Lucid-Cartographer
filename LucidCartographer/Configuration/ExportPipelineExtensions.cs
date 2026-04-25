using LucidCartographer.Services.Export;

namespace LucidCartographer.Configuration;

public static class ExportPipelineExtensions
{
    /// <summary>
    /// Stateless exporters registered as Singleton.
    /// ARCH-HIGH-01: Removed duplicate concrete KmlExporter registration —
    /// consumers depend on IEnumerable&lt;IFileExporter&gt; instead.
    /// </summary>
    public static IServiceCollection AddExportPipeline(this IServiceCollection services)
    {
        services.AddSingleton<IFileExporter, KmlExporter>();
        services.AddSingleton<IFileExporter, GpxExporter>();
        return services;
    }
}
