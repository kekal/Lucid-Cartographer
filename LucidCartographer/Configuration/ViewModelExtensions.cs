using LucidCartographer.Components.Pages;
using LucidCartographer.Components.Shared.Trip;
using LucidCartographer.Services;

namespace LucidCartographer.Configuration;

public static class ViewModelExtensions
{
    /// <summary>
    /// Page ViewModels are registered as Transient to isolate state per page component.
    /// Scoped would incorrectly reuse across navigations within the same SignalR circuit.
    /// </summary>
    public static IServiceCollection AddPageViewModels(this IServiceCollection services)
    {
        services.AddTransient<DataSourcesPageViewModel>();
        services.AddTransient<MapPageViewModel>();
        services.AddTransient<TripViewModel>();
        services.AddTransient<OperationsPageViewModel>();
        services.AddTransient<GoogleSessionPageViewModel>();

        // ViewportService is Scoped (per circuit) so all pages share breakpoint state.
        // AddHttpContextAccessor seeds it from the lucid_viewport cookie during SSR
        // to prevent desktop→mobile flash on initial load.
        services.AddHttpContextAccessor();
        services.AddScoped<ViewportService>();
        return services;
    }
}
