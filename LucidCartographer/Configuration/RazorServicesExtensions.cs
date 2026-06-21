using Microsoft.AspNetCore.ResponseCompression;

namespace LucidCartographer.Configuration;

public static class RazorServicesExtensions
{
    public static IServiceCollection AddRazorAndCompression(this IServiceCollection services)
    {
        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        services.AddResponseCompression(opts =>
        {
            opts.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
                ["application/octet-stream"]); // SignalR uses octet-stream
        });

        return services;
    }
}
