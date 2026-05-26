namespace LucidCartographer.Configuration;

public static class McpServerExtensions
{
    /// <summary>
    /// Registers the Model Context Protocol server with the Streamable-HTTP
    /// transport in stateless mode (each tool call is an independent request,
    /// so scoped services — PoiService, DbContext — resolve per call). Tools are
    /// discovered from this assembly via their [McpServerToolType] attributes.
    /// The endpoint itself is mapped in Program.cs (app.MapMcp).
    /// </summary>
    public static IServiceCollection AddMcpServerServices(this IServiceCollection services)
    {
        services
            .AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithToolsFromAssembly();
        return services;
    }
}
