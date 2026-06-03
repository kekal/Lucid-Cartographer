using LucidCartographer.Services;
using LucidCartographer.Services.Browser;

namespace LucidCartographer.Configuration;

public static class BrowserPipelineExtensions
{
    /// <summary>
    /// Registers the single shared headful Chromium session used by every
    /// Google-account-dependent operation (Saved-List export, "Fetch My Lists",
    /// authenticated scrape). One persistent profile, one browser — so a sign-in
    /// done via the Google session page carries across all automation. The
    /// <see cref="GoogleBrowserLock"/> serialises concurrent operations.
    /// </summary>
    public static IServiceCollection AddBrowserSession(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BrowserOptions>(configuration.GetSection(BrowserOptions.SectionName));

        // Shared single-flight lock around the shared session (export / fetch / scrape).
        services.AddSingleton<GoogleBrowserLock>();

        // The long-lived shared context. Registered once; IBrowserSession resolves
        // to the same instance so its IAsyncDisposable runs on container shutdown.
        services.AddSingleton<BrowserSessionManager>();
        services.AddSingleton<IBrowserSession>(sp => sp.GetRequiredService<BrowserSessionManager>());

        return services;
    }
}
