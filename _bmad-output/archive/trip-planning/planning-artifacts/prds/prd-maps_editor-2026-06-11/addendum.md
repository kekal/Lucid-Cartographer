# Addendum — Trip Planning (technical depth for downstream Architecture / UX)

This file preserves the *technical-how*, rejected alternatives, and research depth that informed the PRD but does not belong in the PRD's capability-level narrative. It is the natural input to `bmad-create-architecture` and `bmad-ux`.

---

## A. Data-model implications (for architecture, not prescriptive)

The PRD speaks in capabilities. The likely physical shape, given the existing schema:

- **`PoiCollectionItem.OrderIndex` (int)** — sequence of a Stop within the Trip. Absent today; this is the one unavoidable migration. Unordered legacy collections get a deterministic seed order (e.g. by `AddedDate`) on first Trip-View open.
- **`PoiCollectionItem.DwellMinutes` (int?, nullable)** — per-stop visit duration. Lives on the join, not on `Poi`, because the same POI may carry different dwell across trips. An overnight hotel is just a large value here (D5).
- **`PoiCollection` trip fields** — `TravelMode` (enum: Any/Air, Drive, Walk, Cycle), `StartPoiId` (nullable), `FinishPoiId` (nullable; null ⇒ roundtrip back to Start), optional `TripStartTime`, and an optional `OptimizationStrategy` marker. `TripViewEnabled` is a UI/view flag — could be persisted per-collection or held as user/view state (UX decision).
- **`RouteSegment` / `PoiEdge` (new entity)** — cache of computed legs keyed by `(FromPoiId, ToPoiId, TravelMode)` with `DurationSeconds`, `DistanceMeters`, `GeometryPolyline` (nullable), `Source` (OSRM | Haversine | Google), `ComputedAt`. Invalidated when either POI's coordinates change. This mirrors the enrichment "computed-state cached on the row" pattern already in the codebase.

These map onto existing patterns: `IDbContextFactory<AppDbContext>`, EF Core migrations, the `Version` concurrency column convention.

## A2. Travel-Time Provider abstraction (basis for D14, the right-altitude resolution)

The PRD specifies travel time at the **provider-contract** altitude, not the engine altitude. The app depends on:

```
TravelTimeProvider.GetLeg(fromStop, toStop, travelMode) -> (duration, distance, Fidelity, geometry?)
```

**Fidelity** ∈ { Measured, Estimated, Placeholder, Manual } is part of the contract — every value is self-describing, so the UI badges trust uniformly regardless of provider.

Candidate implementations (selected per deployment via config; one is active, with Estimated as the universal fallback):

| Provider | Fidelity it yields | Infra | Notes |
|---|---|---|---|
| **Mock (haversine × speed)** | Estimated | none | **Shipping default.** Lets the whole Trip View ship with zero routing infra. |
| **OSRM** | Measured (+geometry) | docker sidecar + OSM extract | The "proper" road-time answer; see §B/§G. |
| **Google-Maps scrape** | Measured-ish | reuses existing Playwright scraper | Most accurate live times, but ToS-gray + fragile — see §C. User's informed call, not a hard no. |
| **Manual entry** | Manual | none | Already in for Air legs (FR-8); generalizes to any leg. |

This is why D3/D4 are no longer blocking PRD decisions: they collapse into *"which Measured provider is the recommended default?"* — an Open Question (§8 OQ1), not a precondition. The Mock keeps v1 shippable while it's pending.

## B. Travel-time engine — full comparison (one provider's options; basis for OQ1)

| Engine | License | Matrix endpoint | Built-in optimization | Verdict |
|---|---|---|---|---|
| **OSRM** | BSD-2-Clause | ✅ `/table` (continental, sub-ms for N≤30) | ❌ | **Chosen.** Fastest matrix, permissive license, trivial Docker. |
| Valhalla | MIT | ✅ `/sources_to_targets` | ⚠️ limited | Fallback if time-of-day/traffic costing ever needed. |
| GraphHopper | Apache core | ❌ (matrix closed-source) | ❌ self-host | Rejected — matrix is commercial-only. |
| OpenRouteService | GPL-ish backend | ✅ matrix | ⚠️ **optimization endpoint NOT in self-hosted build** | Rejected — would still need our own ordering anyway. |
| Google Routes (Compute Route Matrix) | paid SaaS | ✅ | ✅ | **Optional BYO-key only.** Per-element billing ($5–15/1k), 60k elem/min cap; legacy Distance Matrix deprecated 2025-03-01. |

**Licensing note:** all OSM-based engines inherit **ODbL** for the underlying map data → attribution required in the UI. Engine *code* licenses (BSD/MIT/Apache) are permissive for bundling.

## C. Rejected alternative — scraping Google Maps for travel times (basis for D4)

The app already owns a Playwright scraper (used for POI enrichment), so reusing it was tempting. Rejected because:
1. **ToS breach** — Google Maps Platform Terms §3.2.3 forbid extracting/scraping content for use outside Google services. A contract breach (not necessarily illegal for public data per *hiQ v. LinkedIn*), and Google can suspend without warning.
2. **Fragility** — late-2024+ TLS fingerprinting and bot detection require stealth plugins + residential proxies; selectors break constantly. Acceptable for occasional enrichment, unacceptable as a core, frequently-recomputed feature.
3. **Cost/latency** — a headless browser per leg is orders of magnitude slower than an OSRM matrix call.

If a user insists on Google-grade live traffic, the sanctioned path is the **optional BYO Google Routes API key** (D3), which is ToS-clean.

## D. Auto-ordering algorithm (basis for D7)

The "AI orders POIs into logical visiting order" feature is the **Traveling Salesman Problem**; the roundtrip-with-fixed-endpoints case is the standard fixed-depot variant. For N = 5–30 stops it is trivial — no OR-Tools/Concorde:

1. Build the duration matrix (OSRM `/table`, or haversine fallback).
2. **Nearest-neighbor** construction (optionally run from every interior start, keep the best).
3. **2-opt** local search until no improving swap remains. Pin Start and Finish nodes — only interior edges are eligible for swaps. For a roundtrip (Finish = Start), close the loop.
4. Result rewrites `OrderIndex`. ~150 lines of C#, milliseconds at this N.

**LLM/MCP layering:** the deterministic heuristic owns *distance*. An optional LLM pass (driven through the MCP server) reorders by *soft* constraints the cost matrix can't express — opening hours, "museums in the morning", meal timing — then 2-opt runs as cleanup to remove obvious detours the LLM introduced. The matrix heuristic remains the source of truth for travel cost.

## E. Map rendering (basis for D8)

- **Phase 1 — straight connectors.** `L.polyline([latlngs])` over stops in `OrderIndex` order; number the existing markers 1..N. Communicates visiting order with near-zero effort. Pure JS-interop extension to `leafletInterop.js` (`LeafletMap.razor` / `LeafletMapService.cs`).
- **Phase 2 — road geometry.** Request OSRM route geometry (`geometries=geojson` to skip polyline decoding) per leg; replace straight lines with road-shaped lines. Air/Any legs stay as straight (great-circle) lines by design.
- Turnkey option considered: **Leaflet Routing Machine** pointed at self-hosted OSRM gives line rendering + waypoint drag for free, but couples UX to its widget; a thin custom polyline layer keeps control. UX to decide.

## F. MCP / AI surface (extends existing tools)

Existing MCP tools (`Services/Mcp/`) already expose POI CRUD + enrichment. Trip planning adds candidates such as: read ordered stops + computed legs for a collection, trigger travel-time computation, and apply an ordering (rewrite `OrderIndex`) / set Start-Finish / set dwell. This lets an external agent (Claude) perform the soft-constraint ordering described in §D and write the result back through the same authenticated `/mcp` channel.

## G. Deployment / operations notes

- OSRM ships as a **docker-compose sidecar** alongside the existing container; needs a preprocessed OSM extract (region-scoped to keep image/RAM sane — global is large). Profile per travel mode (car/bike/foot) means either multiple OSRM instances or multiple preprocessed datasets — an architecture trade-off.
- OSM data goes stale; a refresh cadence (manual or scheduled) is an operational concern, lower urgency than enrichment freshness.
- Travel-time computation runs as a **background job** mirroring `PoiEnrichmentBackgroundService` (poll/trigger + per-worker DbContext + SQLite write serialization), writing into the `RouteSegment` cache.

## H. Research sources

Wanderlog optimize, Komoot route planner, Google My Maps route model, gis-ops FOSS routing overview, pistack 2026 engine comparison, Valhalla repo, ORS optimization-not-self-hosted thread, Google Maps pricing + Distance-Matrix→Routes migration, Google Maps Platform ToS, scrapehero/Playwright scraping write-ups, NN+2-opt references, Leaflet Routing Machine, maplibre-gl-directions. (Full URLs in the run's research digest.)
