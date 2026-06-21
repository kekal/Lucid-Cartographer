using LucidCartographer.Services;
using LucidCartographer.Services.Browser;

namespace LucidCartographer.Configuration;

public static class BrowserPipelineExtensions
{
    /// <summary>
    /// Registers the single shared headful Chromium session; one persistent profile so Google sign-in carries across all operations.
    /// </summary>
    public static IServiceCollection AddBrowserSession(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BrowserOptions>(configuration.GetSection(BrowserOptions.SectionName));

        services.AddSingleton<GoogleBrowserLock>();

        // IAsyncDisposable runs on container shutdown.
        services.AddSingleton<BrowserSessionManager>();
        services.AddSingleton<IBrowserSession>(sp => sp.GetRequiredService<BrowserSessionManager>());

        return services;
    }
}
