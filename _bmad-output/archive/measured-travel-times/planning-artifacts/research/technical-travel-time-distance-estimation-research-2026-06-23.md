---
stepsCompleted: ['discovery', 'options-analysis', 'decision']
inputDocuments: ['docs/osrm.md', '_bmad-output/project-context.md', 'LucidCartographer/Services/Trip/OsrmTravelTimeProvider.cs', 'LucidCartographer/Services/Trip/ITravelTimeProvider.cs']
workflowType: 'research'
lastStep: 3
research_type: 'technical'
research_topic: 'Travel-time / road-distance estimation between trip stops (replacing the manual self-hosted OSRM pipeline)'
research_goals: 'Find a way to deliver measured road durations/distances in a self-hosted product WITHOUT forcing the operator through the multi-step OSRM data-prep + 3-sidecar pipeline, while keeping the hard privacy guarantee (NFR7: stop coordinates never leave the deployment).'
user_name: 'Yurik'
date: '2026-06-23'
web_research_enabled: true
source_verification: true
decision: 'D (smart-haversine default) + E (Valhalla single-container measured provider); deprecate hand-rolled OSRM; Itinero as fallback only'
---

# Research Report: technical

**Date:** 2026-06-23
**Author:** Yurik
**Research Type:** technical

---

## Research Overview

LucidCartographer computes per-leg travel time/distance through a clean provider seam,
`ITravelTimeProvider` ([ITravelTimeProvider.cs](../../../LucidCartographer/Services/Trip/ITravelTimeProvider.cs)).
Today there are only two providers and a large gap between them:

- **Mock (shipping default)** — haversine straight-line estimate, zero infrastructure, coarse.
- **OSRM (opt-in)** — measured road durations/distances/geometry, but reaching it requires a
  multi-step ops project: download a Geofabrik `.osm.pbf`, run `osrm-extract` / `osrm-partition`
  / `osrm-customize` **once per profile**, run **three** sidecar containers (car/foot/bike — OSRM
  serves exactly one profile per backend), and edit `docker-compose.yml`
  (see [docs/osrm.md](../../../docs/osrm.md)).

The OSRM path is honest but is "an afternoon of ops, not a product feature." The goal of this
research: deliver **measured** road distances/times with a turnkey footprint, **without**
breaking the product's hard privacy guarantee.

### Hard constraint (confirmed by Yurik)

**NFR7 — privacy is a hard requirement.** Stop coordinates must never leave the deployment.
This rules out external routing SaaS (Google Routes / Mapbox / OpenRouteService / GraphHopper
Directions) except as a strictly-consented opt-in. All primary options below are self-hosted.

### Architecture context (already in place — no rework needed)

- New providers slot behind `ITravelTimeProvider` with config selection
  (`TravelTime:Provider`); on any provider failure the off-circuit
  `TravelTimeComputationBackgroundService` degrades to the haversine Estimated value
  (`Source=EstimatedFallback`, `[TRIP-DEGRADE-01]`) — one bad leg never fails the pass.
- Canonical units are fixed at the edges: duration in **seconds**, distance in **meters**.
- An OSM-derived provider must surface ODbL attribution on the map (NFR8) — already wired for OSRM.

---

## Options Considered

| Option | Infra footprint | All modes | Engine maturity | Privacy (NFR7) |
|---|---|---|---|---|
| **D. Smart haversine** | none | yes (estimate) | n/a | ✅ |
| **E. Valhalla (docker-valhalla)** | **1 container, auto-download + auto-build** | **yes — one engine, per-request costing** | high | ✅ |
| A. Turnkey-OSRM | 3 containers + init/prep service | no (one profile per backend) | high | ✅ |
| B. Itinero (in-process .NET) | no docker for routing | yes | **low / single-maintainer risk** | ✅ |
| C. External routing API | none | yes | high | ❌ (breaks NFR7) |

### A. Turnkey-OSRM (automate the existing pipeline)

OSRM is already three compose services behind `--profile osrm`. "Turnkey" = add a one-shot
`osrm-prep` init service that downloads the region `.pbf` (from one env URL) and runs
extract/partition/customize into a shared volume, with the routed containers `depends_on` it.
`docker compose --profile osrm up -d` would then do everything with no manual shell steps.

**Verdict: treats the symptom, not the cause.** OSRM's model is rigid — one backend = one
profile — so three containers and three preprocessing passes remain inherent, and changing a
profile means a full graph rebuild. Superseded by Valhalla.

### E. Valhalla (single self-hosted container) — chosen "measured" engine

C++ engine (originally MapQuest). Two architectural traits hit the exact OSRM pain points:

- **One engine serves all modes** via per-request *dynamic costing* (auto/bike/pedestrian/
  multimodal as a request parameter) — no separate per-profile backend, no rebuild to switch modes.
- **Tile-based, on-demand loading** → lower RAM (~4–8 GB) than OSRM mapping the whole graph.

The mature [`docker-valhalla` (nilsnolde)](https://github.com/nilsnolde/docker-valhalla) image is
turnkey: give it `tile_urls` (a Geofabrik `.pbf` URL) and it **auto-downloads the data and builds
tiles on first start** into a mapped volume, then serves; auto-rebuild on `.pbf` change. One compose
service + one env var replaces "3 containers + 3 preprocessing runs."

```yaml
  valhalla:
    image: ghcr.io/nilsnolde/docker-valhalla/valhalla:latest
    ports: ["8002:8002"]
    volumes: ["./appdata/valhalla:/custom_files"]
    environment:
      - tile_urls=https://download.geofabrik.de/europe/<region>-latest.osm.pbf
      - server_threads=2
    profiles: ["valhalla"]
```

**Privacy:** fully preserved — the `.pbf` is fetched from Geofabrik *at tile-build time*, never per
route; stop coordinates never leave the deployment. ODbL attribution is the same OSM obligation
already wired. Integration cost: write a `ValhallaTravelTimeProvider` against the existing seam
(Valhalla's JSON differs from OSRM's) — same scope as the existing OSRM provider, no architecture
change.

### B. Itinero (in-process .NET routing) — fallback only

.NET-native; routes inside the Blazor process from a `routerdb` built from OSM data — no docker,
single process, perfect privacy. **But maturity is the risk:** stable line is **1.5.1**, the main
repo's last substantive update was **early 2024**, and the "next gen" Itinero 2 has been in
development for years without release ([routing](https://github.com/itinero/routing),
[routing2](https://github.com/itinero/routing2)) — effectively a low-activity single-maintainer
project. Concrete risks: maintenance/abandonment (bugs likely self-fix/fork); less battle-tested
routing edge cases (turn restrictions, one-ways); in-process RAM + startup cost and OOM/crash now
lands in the app process; must verify a clean build under the project's strict analyzer/warnings-as-
errors regime. Keep as a fallback only if a truly docker-free, in-process option is later required.

### C. External routing API — rejected

Trivial to integrate but sends coordinates off-box → **violates NFR7**. Only admissible as a
strictly-consented opt-in provider; not a default and not the primary direction.

---

## Decision

**Adopt D + E.**

1. **D — Smart haversine as the new honest default.** Apply per-mode winding/detour factors
   (e.g. drive ≈ ×1.3) and mode speeds on top of the straight-line distance. Instant, zero infra,
   materially more realistic than a raw straight line. Still an *estimate* (badged as such), not a
   measurement.
2. **E — Valhalla (`docker-valhalla`) as the "measured" opt-in provider.** One auto-building
   container replaces the OSRM mess; all modes from one engine; privacy intact. New
   `ValhallaTravelTimeProvider` behind the existing seam, selected via `TravelTime:Provider=Valhalla`.
3. **Deprecate / simplify the hand-rolled OSRM doc and 3-sidecar setup** (keep OSRM as legacy if
   desired, but it is no longer the recommended measured path).
4. **Itinero — fallback only**, revisited only if a docker-free in-process engine becomes a
   requirement and its maturity risk is acceptable.

This gives the product a clean fidelity ladder — **estimate → good estimate → measured** — entirely
self-hosted, with NFR7 preserved at every rung.

### Open questions for implementation phase

- Valhalla routing accuracy vs OSRM on representative regions; tile build time + disk for the
  operator's region.
- Exact per-mode correction factors for the smart-haversine default (sourced or empirically tuned).
- Whether OSRM is removed outright or retained as a legacy provider.

---

## Sources

- [Itinero routing (GitHub)](https://github.com/itinero/routing)
- [Itinero 2 / routing2 (GitHub)](https://github.com/itinero/routing2)
- [docker-valhalla — nilsnolde (GitHub)](https://github.com/nilsnolde/docker-valhalla)
- [GraphHopper vs OSRM vs Valhalla, self-hosted routing engines compared 2026 (Pi Stack)](https://www.pistack.xyz/posts/2026-04-25-graphhopper-vs-osrm-vs-valhalla-self-hosted-routing-engines-guide-2026/)
- Internal: [docs/osrm.md](../../../docs/osrm.md), [project-context.md](../../project-context.md), [OsrmTravelTimeProvider.cs](../../../LucidCartographer/Services/Trip/OsrmTravelTimeProvider.cs), [ITravelTimeProvider.cs](../../../LucidCartographer/Services/Trip/ITravelTimeProvider.cs)
