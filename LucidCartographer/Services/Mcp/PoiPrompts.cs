using System.ComponentModel;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace LucidCartographer.Services.Mcp;

/// <summary>
/// Reusable MCP prompt templates for common Lucid Cartographer workflows.
/// Clients fetch these via prompts/list and prompts/get; the returned message
/// seeds a conversation that then drives the MCP tools.
/// </summary>
[McpServerPromptType]
public static class PoiPrompts
{
    [McpServerPrompt(Name = "plan_day_trip")]
    [Description("Plan a one-day trip from the POIs already stored in Lucid Cartographer, optionally limited to one collection.")]
    public static ChatMessage PlanDayTrip(
        [Description("Area or city the trip should focus on, e.g. \"Uniejów\" or \"Lower Silesia\".")] string area,
        [Description("Optional collection name to draw candidate POIs from. If omitted, search across everything.")] string? collectionName = null)
    {
        var scope = string.IsNullOrWhiteSpace(collectionName)
            ? "Search across all collections with search_pois."
            : $"Use list_collections to find the collection named \"{collectionName}\", then list_pois_in_collection for its POIs.";

        return new ChatMessage(ChatRole.User,
            $"""
            Plan a realistic one-day itinerary around {area} using the Lucid Cartographer MCP tools.

            1. {scope}
            2. For promising candidates call get_poi to read address, opening notes and rating.
            3. Order the stops by geography to minimise driving, and include rough timing.
            4. Flag any POI that is missing an address or a canonical Google Maps place link,
               and suggest running enrich_poi (or set_poi_google_maps_url) to fix it.

            Present the plan as an ordered list with a one-line reason for each stop.
            """);
    }

    [McpServerPrompt(Name = "audit_collection")]
    [Description("Audit a collection for POIs with missing or low-quality data and re-enrich them.")]
    public static ChatMessage AuditCollection(
        [Description("Name of the collection to audit.")] string collectionName)
    {
        return new ChatMessage(ChatRole.User,
            $"""
            Audit the Lucid Cartographer collection "{collectionName}" for data quality.

            1. Resolve the collection id with list_collections, then list_pois_in_collection.
            2. For each POI call get_poi and flag any that lack an address, a photo
               (hasStoredImage = false), or a canonical /maps/place/ Google Maps URL.
            3. For each flagged POI, call enrich_poi to re-run enrichment (this is idempotent —
               it discards the current link and searches again).
            4. If enrichment still cannot resolve a place, ask me for the correct Google Maps
               URL and apply it with set_poi_google_maps_url.

            Finish with a short report: how many POIs were checked, fixed, and still need a manual URL.
            """);
    }
}
