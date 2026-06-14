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
        3. Organize: create_collection; create_poi (stores fields as-is and does NOT
           enrich — call enrich_poi afterwards for a real Google place; ideal for
           events / custom points); move_poi / copy_poi / delete_poi between
           collections.
        4. Enrich: enrich_poi re-runs enrichment for a POI and is idempotent —
           calling it again discards the current Google Maps link and runs a fresh
           name search. If a place still cannot be resolved automatically, supply the
           correct link with set_poi_google_maps_url. Track progress with
           get_enrichment_status (remaining reaches 0 when the queue drains).

        Avoiding duplicates:
        - search_pois matches the query as ONE string (phrase/substring), so pass only ONE
          place name per call. A query with several different names returns [] even when each
          exists separately.
        - An empty result means "this single name was not found", NOT "search is broken".
        - Before create_poi, search the name; on a hit, get_poi to inspect its `collections`.
          A POI can live in several collections — prefer copy_poi/move_poi over re-creating one.

        Trips (ordering a collection into a route):
        - get_trip reads the ordered Stops (1-based OrderIndex, Start/Finish flags,
          dwell minutes) + the cached Legs (seconds/meters) under the collection's
          Travel Mode.
        - assign_stop_order sets the full visiting order — pass ALL placeable Stop
          PoiIds once, in order. set_trip_start / set_trip_finish pin the ends
          (Start→1, Finish→last; clear_* to release); set_dwell_time sets per-Stop
          dwell minutes. An MCP-assigned order persists exactly like a manual drag and
          stays drag-editable. Honor soft constraints the cost matrix can't express
          ("museums in the morning, rooftop bar last").

        Allowed category values: restaurant, cafe, bar, hotel, attraction, shopping,
        nature, other.
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
