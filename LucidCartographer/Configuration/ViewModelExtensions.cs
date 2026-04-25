using LucidCartographer.Components.Pages;

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
        return services;
    }
}
