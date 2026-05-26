namespace LucidCartographer.Configuration;

public static class McpServerExtensions
{
    /// <summary>
    /// Server-level usage guidance returned in the MCP initialize response
    /// (the protocol's <c>instructions</c> field). Gives a client an overview
    /// of the toolset before it inspects individual tool descriptions.
    /// </summary>
    private const string ServerInstructions =
        """
        Lucid Cartographer MCP server — manage geographic Points of Interest (POIs)
        and the collections that group them.

        Typical workflow:
        1. Discover: list_collections, then list_pois_in_collection or search_pois.
        2. Inspect: get_poi for every field; get_poi_image for the stored photo.
        3. Organize: create_collection; create_poi (a new POI is always created
           unenriched and queued for background enrichment); move_poi / copy_poi /
           delete_poi between collections.
        4. Enrich: enrich_poi re-runs enrichment for a POI and is idempotent —
           calling it again discards the current Google Maps link and runs a fresh
           name search. If a place still cannot be resolved automatically, supply the
           correct link with set_poi_google_maps_url. Track progress with
           get_enrichment_status (remaining reaches 0 when the queue drains).

        Allowed category values: restaurant, cafe, bar, hotel, attraction, shopping,
        nature, other. Allowed status values: visited, want_to_go, imported.
        Coordinate ranges: latitude [-90, 90], longitude [-180, 180].

        Reference resources: lucid://guide/usage and lucid://reference/poi-schema.
        """;

    /// <summary>
    /// Registers the Model Context Protocol server with the Streamable-HTTP
    /// transport in stateless mode (each tool call is an independent request,
    /// so scoped services — PoiService, DbContext — resolve per call). Exposes
    /// tools, prompt templates, reference resources and server-level
    /// instructions. The endpoint itself is mapped in Program.cs (app.MapMcp).
    /// </summary>
    public static IServiceCollection AddMcpServerServices(this IServiceCollection services)
    {
        services
            .AddMcpServer(options => options.ServerInstructions = ServerInstructions)
            .WithHttpTransport(options => options.Stateless = true)
            .WithToolsFromAssembly()
            .WithPromptsFromAssembly()
            .WithResourcesFromAssembly();
        return services;
    }
}
