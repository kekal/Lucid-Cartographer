using System.ComponentModel;
using ModelContextProtocol.Server;

namespace LucidCartographer.Services.Mcp;

/// <summary>
/// Static reference documents the MCP client can read via resources/list and
/// resources/read. These give an agent the background it needs to use the
/// tools correctly without trial and error.
/// </summary>
[McpServerResourceType]
public static class PoiResources
{
    [McpServerResource(UriTemplate = "lucid://guide/usage", Name = "usage-guide", MimeType = "text/markdown")]
    [Description("How to use the Lucid Cartographer MCP server: the typical read/organize/enrich workflow.")]
    public static string UsageGuide() =>
        """
        # Lucid Cartographer — usage guide

        Lucid Cartographer manages geographic Points of Interest (POIs) grouped into
        collections. A POI may belong to several collections at once.

        ## Typical workflow
        1. **Discover** — `list_collections`, then `list_pois_in_collection` or `search_pois`.
        2. **Inspect** — `get_poi` returns every field; `get_poi_image` returns the stored photo
           as viewable image content (or the external image URL as text if no bytes are stored).
        3. **Organize** — `create_collection`; `create_poi` (a new POI is always created
           *unenriched* and queued for background enrichment); `move_poi`, `copy_poi` and
           `delete_poi` move POIs between collections. `delete_poi` only unlinks from one
           collection; a POI left in no collection is removed entirely.
        4. **Enrich** — `enrich_poi` re-runs enrichment for one POI and is **idempotent**:
           calling it again discards the current Google Maps link and runs a fresh name search.
           `enrich_collection` queues every POI in a collection. If a place still cannot be
           resolved automatically, supply the correct link with `set_poi_google_maps_url`.
           Poll `get_enrichment_status` until `remaining` reaches 0.

        ## Notes
        - Enrichment scrapes address, website, phone and a photo from the POI's Google Maps URL,
          or — when there is no URL — from a Google Maps search of the POI name (biased by its
          coordinates).
        - A POI is considered properly located once it has a canonical `/maps/place/` URL.

        See `lucid://reference/poi-schema` for field definitions and allowed values.
        """;

    [McpServerResource(UriTemplate = "lucid://reference/poi-schema", Name = "poi-schema", MimeType = "text/markdown")]
    [Description("Reference for POI fields, allowed category/status values and coordinate ranges.")]
    public static string PoiSchema() =>
        """
        # POI schema reference

        ## Fields returned by get_poi
        - `id` (int), `name` (string, required)
        - `latitude` / `longitude` (nullable; lat in [-90, 90], lon in [-180, 180])
        - `googleMapsUrl` (nullable) — canonical link is a `/maps/place/...` URL
        - `address`, `category`, `notes`, `website`, `phone` (nullable strings)
        - `rating` (1–5, personal), `googleRating` (1.0–5.0), `reviewCount` (>= 0)
        - `country`, `region` (nullable strings)
        - `imageUrl` (external), `hasStoredImage` (bool), `imageEndpoint` (path for get_poi_image)
        - `addedDate`
        - `isEnriched`, `enrichmentNeedsManualUrl`, `enrichmentFailureCount`
        - `collections` (names this POI belongs to)

        ## Allowed `category` values
        restaurant, cafe, bar, hotel, attraction, shopping, nature, other (or omitted)

        ## Validation enforced on create/update
        - name: required, <= 500 chars
        - latitude in [-90, 90], longitude in [-180, 180]
        - rating in [1, 5], googleRating in [1.0, 5.0], reviewCount >= 0
        - category must be one of the allowed values above
        """;
}
