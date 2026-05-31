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
        3. **Organize** — `create_collection`; `create_poi` stores the fields you pass
           *as-is* and does **not** enrich (no Google lookup, no fuzzy-merge into a nearby
           place) — ideal for events / custom waypoints. To fill address/photo/website/phone
           for a real place, call `enrich_poi` on the new id afterwards. Optionally pass
           `imageUrl` (http/https) to `create_poi`/`edit_poi` to download and store a photo.
           `move_poi`, `copy_poi` and `delete_poi` move POIs between collections. `delete_poi`
           only unlinks from one collection; a POI left in no collection is removed entirely.
        4. **Enrich** — `enrich_poi` re-runs enrichment for one POI and is **idempotent**:
           calling it again discards the current Google Maps link and runs a fresh name search.
           `enrich_collection` queues every POI in a collection. If a place still cannot be
           resolved automatically, supply the correct link with `set_poi_google_maps_url`.
           Poll `get_enrichment_status` until `remaining` reaches 0.

        ## Searching & deduplicating
        - `search_pois` matches the query as a **single string** (phrase/substring) across
          name/address/notes/tags — every word must occur in the *same* POI. Pass **one** place
          name per call; a query with several different names (e.g.
          `"Energylandia Suntago Mandoria"`) returns `[]` even when each exists separately.
        - An empty result means "this single name was not found", **not** "search is broken".
          If you batched several names and got `[]`, retry one name at a time.
        - Before `create_poi`, search the name to avoid duplicates. On a hit, call `get_poi(id)`
          and read its `collections` to see where it already lives; a POI can belong to several
          collections, so prefer `copy_poi`/`move_poi` over re-creating it.

          ```
          for name in candidates:
              if not search_pois(name):   # single name; empty ⇒ genuinely absent
                  create_poi(...)
          ```

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
        - `isEnriched` (enrichment has run to completion), `enrichmentRequested` (queued for
          the background worker), `enrichmentNeedsManualUrl`, `enrichmentFailureCount`
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
