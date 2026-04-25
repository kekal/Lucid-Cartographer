using LucidCartographer.Services;
using LucidCartographer.Services.Operations;

namespace LucidCartographer.Configuration;

public static class PoiServicesExtensions
{
    public static IServiceCollection AddPoiServices(this IServiceCollection services)
    {
        services.AddScoped<IPoiService, PoiService>();
        services.AddScoped<IPoiMatcher, PoiMatcher>();
        services.AddScoped<ISetOperationService, SetOperationService>();
        services.AddScoped<IMapService, LeafletMapService>();
        return services;
    }
}
