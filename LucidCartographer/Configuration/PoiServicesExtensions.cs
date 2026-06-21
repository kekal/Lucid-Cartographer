using LucidCartographer.Services;
using LucidCartographer.Services.Operations;

namespace LucidCartographer.Configuration;

public static class PoiServicesExtensions
{
    /// <summary>
    /// HttpClient for image downloads with isolated timeout, user-agent, and response-size cap.
    /// </summary>
    public const string ImageDownloadClient = "image-download";

    public static IServiceCollection AddPoiServices(this IServiceCollection services)
    {
        services.AddScoped<IPoiService, PoiService>();
        services.AddScoped<IPoiMatcher, PoiMatcher>();
        services.AddScoped<ISetOperationService, SetOperationService>();
        services.AddScoped<IMapService, LeafletMapService>();

        services.AddHttpClient(ImageDownloadClient, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(20);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("LucidCartographer/1.0 (+image-fetch)");
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            // Follow redirects but cap them — bounds redirect-loop DoS. Note: the
            // http/https scheme check in ImageDownloadHelper is first-hop only;
            // a redirect can still reach an internal host (accepted, same SSRF
            // posture as a direct fetch — see the helper's SECURITY note).
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
        });

        return services;
    }
}
