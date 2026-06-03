using LucidCartographer.Components.Pages;
using LucidCartographer.Services;

namespace LucidCartographer.Configuration;

public static class ViewModelExtensions
{
    /// <summary>
    /// Page ViewModels are registered as Transient: a fresh instance per
    /// page-component instance, matching WPF's "new VM per window" semantic.
    /// Scoped would reuse across navigations within the same SignalR circuit,
    /// which is wrong for page-scoped state.
    /// </summary>
    public static IServiceCollection AddPageViewModels(this IServiceCollection services)
    {
        services.AddTransient<DataSourcesPageViewModel>();
        services.AddTransient<MapPageViewModel>();
        services.AddTransient<OperationsPageViewModel>();
        services.AddTransient<GoogleSessionPageViewModel>();

        // Viewport width tracker is Scoped (one per circuit) so the layout and
        // every page agree on the current desktop/mobile breakpoint and share
        // the same change notifications. AddHttpContextAccessor lets the
        // service seed itself from the `lucid_viewport` cookie during the
        // initial SSR pass, eliminating the desktop→mobile flash on phones.
        services.AddHttpContextAccessor();
        services.AddScoped<ViewportService>();
        return services;
    }
}
