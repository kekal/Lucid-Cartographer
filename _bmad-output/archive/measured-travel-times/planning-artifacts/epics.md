---
stepsCompleted: [1, 2, 3, 4]
status: 'complete'
completedAt: '2026-06-23'
inputDocuments:
  - _bmad-output/planning-artifacts/prds/prd-maps_editor-2026-06-23/prd.md
  - _bmad-output/planning-artifacts/prds/prd-maps_editor-2026-06-23/addendum.md
  - _bmad-output/planning-artifacts/architecture.md
  - _bmad-output/project-context.md
project_name: 'maps_editor'
user_name: 'Yurik'
date: '2026-06-23'
---

# maps_editor - Epic Breakdown

## Overview

This document provides the complete epic and story breakdown for **Measured Travel-Time & Distance Estimation** (smart-haversine default + self-hosted Valhalla measured provider, replacing the hand-rolled OSRM path), decomposing the requirements from the PRD and Architecture Decision Document into implementable stories. This is a **brownfield delta** inside the existing LucidCartographer Blazor Server app — the provider seam, background service, directional `RouteSegment` cache, DI two-overload pattern, and canonical-units discipline are already built and must not be reworked. No UX Design specification exists (correctly absent — operator-facing infrastructure, no new end-user workflow).

## Requirements Inventory

### Functional Requirements

**Feature A — Smart-haversine default (the honest default rung)**

FR-1: The default provider computes a per-leg ground estimate by applying a per-mode detour/winding factor to the great-circle (haversine) distance, then deriving duration from the existing per-mode speed (today the default applies per-mode speed to the raw straight-line distance with no detour factor).
FR-2: Detour factors and per-mode speeds are operator-configurable via the existing `TravelTime` config section and ship with documented sane defaults (`[ASSUMPTION]` Drive ≈ ×1.3, Cycle ≈ ×1.2, Walk ≈ ×1.15; exact values sourced/tuned in implementation).
FR-3: Smart-haversine ground legs are badged **Estimated** (not Measured) and report the adjusted distance (meters) and duration (seconds) through the existing seam. Air/AnyAir legs remain **Placeholder** ("—"), unchanged.
FR-4: The provider-failure degrade path uses the same smart-haversine computation, so a degraded leg matches the default rung's accuracy and remains honestly badged (`Source=EstimatedFallback`, Fidelity Estimated). `[TRIP-DEGRADE-01]` is preserved.

**Feature B — Valhalla measured provider**

FR-5: A new `ValhallaTravelTimeProvider` implements `ITravelTimeProvider`, is selected via `TravelTime:Provider=Valhalla`, and returns measured road duration (seconds), distance (meters), and route geometry.
FR-6: One Valhalla engine serves all ground modes via per-request dynamic costing; the provider maps Drive/Walk/Cycle to Valhalla's costing models (auto / pedestrian / bicycle). No per-mode backend and no rebuild to switch modes.
FR-7: Measured legs are badged **Measured** and carry the road-geometry polyline for map display, matching today's OSRM-era behavior.
FR-8: On any Valhalla failure (unreachable, timeout, no-route), the leg degrades to smart-haversine (`Source=EstimatedFallback`); one bad leg never fails the background pass.
FR-9: Air/AnyAir legs are not routed through Valhalla (remain Placeholder), matching the existing ground-only computation policy.
FR-10: The Valhalla provider declares its own OSM/ODbL attribution, which surfaces on the map through the existing attribution wiring (NFR8). When the default (smart-haversine) provider is active, no routing attribution is shown (not OSM-derived).

**Feature C — Turnkey deployment**

FR-11: A single `docker-valhalla` compose service (under a `valhalla` profile) replaces the three OSRM sidecars. It auto-downloads the region `.pbf` from a configured URL and builds tiles on first start into a mapped volume, then serves; it auto-rebuilds when the `.pbf` changes.
FR-12: The operator enables measured routing with exactly: start the profile (`docker compose --profile valhalla up`), set the region via one env var (`tile_urls`), and set `TravelTime:Provider=Valhalla`. No manual `extract`/`partition`/`customize` steps and no per-profile setup.
FR-13: Operator documentation (replacing `docs/osrm.md`) describes the turnkey setup, region selection, expected one-time tile-build cost (time / disk / RAM for the operator's region), and restates the privacy guarantee.
FR-13a: **First-boot / tile-build window.** While Valhalla is building tiles (first start, and on every `.pbf` auto-rebuild) it is unreachable; during this window measured routing is not yet available and ground legs degrade to the smart-haversine estimate (Fidelity Estimated) rather than erroring or hanging the trip view. The condition must be operator-visible (at minimum a clear startup/health log line stating tiles are building and routing is temporarily estimated), and the operator doc (FR-13) must set the expectation. The background pass re-attempts measured routing once Valhalla becomes reachable (legs recomputed when the cache holds only an Estimated/fallback row, never overwriting Manual/Measured).

**Feature D — OSRM removal & migration**

FR-14: Remove all hand-rolled OSRM artifacts: `OsrmTravelTimeProvider`, `OsrmOptions`, `OsrmRouteUnavailableException`, the OSRM branch in `TripServicesExtensions`, the `TravelTimeSource.Osrm` constant, the named `"osrm"` `HttpClient` registration, the three `osrm-*` compose services + their commented env block, OSRM-specific tests, and `docs/osrm.md`.
FR-15: `TravelTime:Provider=Osrm` is no longer recognized. When an unknown/retired provider id is configured, the app falls back to the smart-haversine default rather than failing to boot — but because this silently downgrades a deployment from Measured to Estimated, the fallback must be prominently surfaced: a high-level startup warning naming the retired value and a release-note / migration-doc callout.
FR-16: Existing cached `RouteSegment` rows produced by OSRM (`Source=OSRM`, Fidelity Measured) are invalidated on a one-time migration so they are recomputed by the active provider rather than persisting as stale, un-reproducible measurements that the never-downgrade-Measured guard (`[TRIP-MANUAL-01]`) would otherwise pin indefinitely. `Manual` rows are never touched.

**Feature E — Fidelity ladder & badging**

FR-17: The trip view presents a coherent fidelity ladder using the existing badges — **Estimated** (smart-haversine: default and fallback), **Measured** (Valhalla), **Manual** (user-entered), and **Placeholder / "—"** for un-routable Air. No new badge type is introduced; the smart-haversine upgrade improves the accuracy behind the Estimated rung rather than adding a third visible tier.

### NonFunctional Requirements

NFR7: **Privacy (HARD CONSTRAINT, non-negotiable).** Stop coordinates must never leave the deployment at any fidelity rung. Smart-haversine computes in-process; Valhalla routes against locally built tiles, fetching the `.pbf` from Geofabrik only at tile-build time (never per route). The constraint must be designed-in (only permitted outbound access from the Valhalla container is the build-time `.pbf` fetch; routing requests reach Valhalla over the internal compose network only; pin the third-party image as the trust boundary) and **verified**: (a) an automated check that the active provider issues no per-route outbound call carrying coordinates, and (b) a documented operator check that no stop-coordinate egress occurs during normal routing.
NFR8: **Attribution.** When an OSM-derived provider (Valhalla) is active, its OSM/ODbL routing attribution must surface on the map's attribution control. The smart-haversine default declares no routing attribution. The OSRM attribution string is replaced by a Valhalla one.
NFR-9: **Performance / footprint.** Valhalla's tile-based on-demand loading targets lower RAM (~4–8 GB) than OSRM's full-graph mapping. Tile build is a one-time cost per region (and per `.pbf` change), incurred off the request path. The off-circuit background computation service is unchanged; routing latency must not block the Blazor circuit.
NFR-10: **Reliability / graceful degradation.** A single leg's provider failure (including the whole tile-build window, FR-13a) degrades to the smart-haversine estimate and never fails the batch (`[TRIP-DEGRADE-01]`). The cache upsert must never downgrade a Manual or Measured row (`[TRIP-MANUAL-01]`); conversely, an Estimated/EstimatedFallback row must remain eligible for later upgrade to a measured value once the provider is reachable.
NFR-11: **Canonical units.** Duration in seconds, distance in meters, fixed at the provider edge; no mid-layer conversion. Unchanged by this feature.
NFR-12: **Build discipline.** The new provider and config must compile clean under the project's `TreatWarningsAsErrors` + analyzer regime; no new group-B analyzer violations (MA0002/MA0015/MA0046/MA0047/MA0074/VSTHRD200).
NFR-13: **DI seam integrity.** The parameterless `AddTripServices()` overload registers the smart-haversine default (what the integration host composes by hand); the `IConfiguration` overload adds the config-selected Valhalla provider. The Trip integration test filter must pass after the DI change (recurring integration-host regression point).

### Additional Requirements

Technical requirements from the Architecture Decision Document (ADs) that shape epics/stories:

- **No starter template** — brownfield delta into the existing .NET 8 / C# 14 (LangVersion 14, .NET 10 SDK) / Blazor Server / EF Core 8 + SQLite solution. First implementation step is code (AD-1 smart-haversine), not a scaffold.
- **No schema change / no EF migration** — `RouteSegment`, its directional key `[TRIP-CACHE-01]`, and the `Fidelity`/`TravelMode` string-constant + EF-check-constraint model `[TRIP-SCHEMA-01]` are reused verbatim. Valhalla legs reuse the existing `Measured` Fidelity (no new Fidelity member, no check-constraint change). `TravelTimeSource` adds `Valhalla` and removes `Osrm` (provenance string only, not DB-constrained).
- **AD-1** — smart-haversine lives in the single `EstimatedTravelTime.Compute` edge (reused by both `MockTravelTimeProvider` and the background fallback); detour factors added to `TravelTimeOptions`. **Critical guard `[RD3]`:** the TSP cost matrix (`DistanceMatrixService`) must keep using raw haversine — never the detour-adjusted distance.
- **AD-2** — broaden the background-service pending-leg trigger from "no row exists" to "no row exists OR upgrade-eligible" (upgrade-eligible = Fidelity ∈ {Estimated, Placeholder} AND Source ∈ {Mock, EstimatedFallback}), gated on a new `ITravelTimeProvider.ProducesMeasuredFidelity` capability bool (Mock=false, Valhalla=true) to prevent perpetual Mock rework. Upsert guard unchanged. Three-site leg-projection mirror must stay aligned.
- **AD-3** — Valhalla provider contract: new `ValhallaTravelTimeProvider` / `ValhallaOptions` / `ValhallaRouteUnavailableException`; costing map Drive→auto, Walk→pedestrian, Cycle→bicycle; single `/route` POST per leg; units converted at edge (time→int seconds, length km→×1000 meters); geometry **precision 6** (polyline6) and the map decoder precision MUST match; missing/blank geometry on a Measured leg throws; named `"valhalla"` HttpClient; `ValhallaOptions` BaseUrl=`http://valhalla:8002`, RequestTimeoutSeconds=10, GeometryPrecision=6; coordinates `{lat,lon}` JSON, formatted invariant-culture.
- **AD-4** — DI: replace the `=="Osrm"` branch with a `=="Valhalla"` branch in `AddTripServices(IConfiguration)`; parameterless overload keeps Mock. Run the Trip integration filter after.
- **AD-5** — NFR7 containment design + automated no-egress test (default issues no outbound HTTP; Valhalla targets only the configured internal base URL) + documented operator verification procedure.
- **AD-6** — FR-16 one-time OSRM-row purge in `StartupCleanupService.cs`: delete every `RouteSegment` where `Source == "OSRM"` (literal string — the constant is deleted) under `SqliteWriteLock`, never touching Manual rows; idempotent/self-retiring; log the count.
- **AD-7** — retired/unknown provider id → warn + fallback (not fail-fast), wired into the DI fallthrough.
- **AD-8** — single `valhalla` compose service under a `valhalla` profile; remove three `osrm-*` services + commented OSRM env; pin the image by immutable tag (ideally `@sha256:` digest), never `:latest`; auto-download + auto-tile-build into `./appdata/valhalla:/custom_files`; expose 8002.
- **AD-9** — replace the OSRM attribution string with a Valhalla ODbL string in `UiStrings.cs` (`TripRoutingAttributionOsm` → `TripRoutingAttributionValhalla`), wired through the unchanged provider.Attribution → VM → MapPage → LeafletMap chain. Must land before/with FR-14 deletes the OSRM reference.
- **Implementation sequence (lowest-risk first, from Decision Impact Analysis):** AD-1 → AD-9 + Source constant → AD-3 → AD-2 → AD-4 → AD-6 → AD-7 → AD-8 + FR-14 deletions + AD-5 no-egress test + FR-13 docs.
- **Cross-dependencies:** AD-2 depends on AD-3's `ProducesMeasuredFidelity` (define interface member first); AD-6 depends on AD-3/AD-4 active; AD-9 before/with FR-14.
- **Empirical value-tuning deferred to implementation (not blockers):** OQ-1 detour-factor values; OQ-2 SM-3 accuracy thresholds T₁/T₂; OQ-3 tile build time/disk/RAM; OQ-7 exact pinned image tag/digest.

### UX Design Requirements

_None — no UX Design specification exists for this feature. It is operator-facing infrastructure (providers/config/compose) with no new end-user workflow; the trip planner experiences it only as better numbers and correct fidelity badges in the existing trip view. No new UI controls, no new badge type (FR-17 reuses existing badges)._

### FR Coverage Map

- FR-1: Epic 1 — apply per-mode detour factor to haversine in the single estimate path
- FR-2: Epic 1 — operator-configurable detour factors + speeds under `TravelTime`, documented defaults
- FR-3: Epic 1 — Estimated badge + adjusted distance/duration; Air/AnyAir stays Placeholder
- FR-4: Epic 1 — degrade path reuses the same smart-haversine computation (`[TRIP-DEGRADE-01]`)
- FR-5: Epic 2 — `ValhallaTravelTimeProvider` returns measured seconds/meters/geometry
- FR-6: Epic 2 — one engine serves all ground modes via costing map (auto/pedestrian/bicycle)
- FR-7: Epic 2 — Measured badge + road-geometry polyline on the map
- FR-8: Epic 2 — Valhalla failure degrades to smart-haversine; one bad leg never fails the pass
- FR-9: Epic 2 — Air/AnyAir not routed through Valhalla (Placeholder)
- FR-10: Epic 2 — Valhalla OSM/ODbL attribution surfaces; default shows none (NFR8)
- FR-11: Epic 2 — single auto-building `docker-valhalla` compose service under a `valhalla` profile
- FR-12: Epic 2 — turnkey enable: start profile + `tile_urls` + `TravelTime:Provider=Valhalla`
- FR-13: Epic 2 — operator documentation replacing `docs/osrm.md`
- FR-13a: Epic 2 — tile-build window degrades to estimate, operator-visible, self-heals (AD-2 recompute trigger)
- FR-14: Epic 3 — remove all hand-rolled OSRM artifacts
- FR-15: Epic 3 — retired/unknown provider id warns + falls back (not fail-fast)
- FR-16: Epic 3 — one-time invalidation of `Source=OSRM` cache rows; Manual rows untouched
- FR-17: Epic 1 + Epic 2 — coherent two-badge fidelity ladder (Estimated rung lands in Epic 1, Measured rung completes the ladder in Epic 2); no new badge type

## Epic List

### Epic 1: Smarter honest default (smart-haversine)
Operators and trip planners get a materially more accurate zero-infrastructure default for free — per-mode detour/winding factors are applied to the great-circle distance so ground legs no longer systematically under-estimate, while staying honestly badged **Estimated**. This single change also upgrades the universal provider-failure fallback to the same accuracy, since both share one estimate code path. Standalone (no infra, no Valhalla); it is also the foundation the later degrade paths depend on.
**FRs covered:** FR-1, FR-2, FR-3, FR-4 (and the Estimated rung of FR-17)

### Epic 2: Self-hosted Valhalla measured routing (turnkey)
Operators can stand up **measured** road durations/distances with a turnkey footprint — one auto-building container plus one env var — replacing the old "afternoon of ops." Delivers the measured provider end-to-end behind the existing seam: all-mode dynamic costing, Measured badging with map geometry + ODbL attribution, graceful degradation to the Epic 1 estimate (including the first-boot tile-build window that self-heals once tiles finish), the capability-gated recompute trigger, config/DI selection, the compose service, the no-egress privacy verification, and operator documentation. Builds on Epic 1's estimate as its degrade target; does not require Epic 3.
**FRs covered:** FR-5, FR-6, FR-7, FR-8, FR-9, FR-10, FR-11, FR-12, FR-13, FR-13a, FR-17 (Measured rung / ladder completion); NFR7, NFR8, NFR-9, NFR-13

### Epic 3: Retire OSRM (removal & migration)
Removes the superseded hand-rolled OSRM path cleanly and safely: deletes all OSRM artifacts (provider/options/exception/DI branch/HttpClient/compose sidecars/tests/docs), makes a now-retired `TravelTime:Provider=Osrm` warn-and-fall-back rather than brick boot, and runs a one-time migration that invalidates stale `Source=OSRM` cache rows so they recompute under the active provider — never touching Manual rows. Strictly follows Epic 2 (Valhalla must exist as the replacement before OSRM is removed).
**FRs covered:** FR-14, FR-15, FR-16

---

## Epic 1: Smarter honest default (smart-haversine)

Upgrade the zero-infrastructure default provider so per-leg ground estimates apply a per-mode detour/winding factor to the great-circle distance before deriving duration — materially more realistic, still badged honestly as **Estimated**, still no infrastructure. Because the default and the universal provider-failure fallback share the single `EstimatedTravelTime.Compute` edge, this one change improves both rungs at once. This epic stands alone (ships value with no Valhalla, no infra) and is the foundation every later degrade path depends on. Per `[RD3]`, the TSP cost matrix must remain on raw haversine.

### Story 1.1: Configurable per-mode detour factors

As a deployment operator,
I want per-mode detour/winding factors I can configure (with sane shipped defaults),
So that I can tune how realistically the default estimate reflects real road distance for my region without touching code.

**Acceptance Criteria:**

**Given** the `TravelTime` config section already binds per-mode speeds into `TravelTimeOptions`
**When** I add per-mode detour factors (Drive, Cycle, Walk) to `TravelTimeOptions` and to `appsettings.json`
**Then** each factor is bindable from the existing `TravelTime` section (e.g. `TravelTime:DriveDetourFactor`)
**And** documented defaults ship as `[ASSUMPTION]` values Drive ×1.3, Cycle ×1.2, Walk ×1.15 (FR-2)
**And** a `DetourFactorFor(mode)` accessor mirrors the existing `SpeedFor(mode)` shape and returns the configured (or default) factor for any ground mode
**And** the build is clean under `TreatWarningsAsErrors` with no group-B analyzer violations (NFR-12).

### Story 1.2: Apply the detour factor in the single estimate path

As a trip planner,
I want ground-leg estimates to account for road winding,
So that the default trip times and distances stop systematically under-estimating real travel.

**Acceptance Criteria:**

**Given** `EstimatedTravelTime.Compute` is the sole estimate edge, reused by both `MockTravelTimeProvider` (default) and the background-service fallback
**When** I apply `adjustedDistance = haversine × DetourFactorFor(mode)` and then `duration = adjustedDistance ÷ SpeedFor(mode)` in that one method
**Then** the default provider reports the **adjusted** distance (meters) and duration (seconds) through the existing seam (FR-1, FR-3)
**And** ground legs remain badged **Estimated** and Air/AnyAir legs remain **Placeholder** ("—"), unchanged (FR-3)
**And** the provider-failure degrade path produces the identical smart-haversine value with `Source=EstimatedFallback`, preserving `[TRIP-DEGRADE-01]` (FR-4)
**And** canonical units stay seconds/meters with conversion only at the edge (NFR-11)
**And** unit tests assert the adjusted distance/duration for each ground mode and that Air stays Placeholder.

### Story 1.3: Keep trip ordering mode-invariant (TSP guard)

As a trip planner,
I want the stop ordering to stay stable and independent of the new detour factors,
So that improving estimate accuracy never silently changes my computed route order.

**Acceptance Criteria:**

**Given** the TSP cost matrix (`DistanceMatrixService`) is built from raw straight-line/haversine distance and ordering is mode-invariant (`[RD3]`)
**When** the smart-haversine detour factor is introduced in the estimate path
**Then** the cost matrix continues to use **raw** haversine and is never routed through the detour-adjusted distance (AD-1 critical guard)
**And** the NN+2-opt ordering output is unchanged for a fixed set of stops regardless of detour-factor configuration
**And** a regression test pins that `assign_stop_order`/`SetOrderAsync` results do not vary with detour-factor values.

---

## Epic 2: Self-hosted Valhalla measured routing (turnkey)

Deliver measured road durations/distances end-to-end behind the existing `ITravelTimeProvider` seam via a new self-hosted Valhalla provider, with a turnkey footprint (one auto-building container + one env var). Includes the all-mode dynamic-costing provider, Measured badging with map geometry + ODbL attribution, the capability-gated recompute trigger, config/DI selection, the compose service, the first-boot tile-build degrade (self-healing), the NFR7 no-egress verification, and operator documentation. Builds on Epic 1's smart-haversine as its degrade target; does not require Epic 3. The OSRM provider files remain present-but-dead after this epic and are removed in Epic 3.

### Story 2.1: Provider capability seam + Valhalla source & attribution scaffolding

As an implementing developer,
I want the seam-level scaffolding for a measured provider in place,
So that the Valhalla provider and the recompute trigger can be built against a stable contract.

**Acceptance Criteria:**

**Given** `ITravelTimeProvider` today exposes `Source`, `Attribution`, and `GetLegAsync`
**When** I add a `bool ProducesMeasuredFidelity` member to the interface
**Then** `MockTravelTimeProvider` implements it returning `false` and continues to declare `Attribution=null` (AD-2)
**And** `TravelTimeSource` gains `public const string Valhalla = "Valhalla"` (the existing `Osrm` constant is left untouched here; its removal is Epic 3) (AD-3)
**And** a new Valhalla ODbL routing-attribution string is added to `UiStrings.cs` (e.g. `TripRoutingAttributionValhalla = "Routing © Valhalla · Map data © OpenStreetMap contributors (ODbL)"`) alongside the existing OSRM string (AD-9, NFR8)
**And** the solution compiles clean under the analyzer regime with no group-B violations (NFR-12).

### Story 2.2: ValhallaTravelTimeProvider (measured, all ground modes)

As a deployment operator,
I want a Valhalla-backed provider that returns measured duration, distance, and road geometry for every ground mode,
So that trip legs can show real road-network travel times instead of estimates.

**Acceptance Criteria:**

**Given** the seam scaffolding from Story 2.1 and a reachable Valhalla engine
**When** I add `ValhallaTravelTimeProvider`, `ValhallaOptions`, and `ValhallaRouteUnavailableException` in `Services/Trip/` (sealed, primary-constructor DI, mirroring the OSRM provider shape)
**Then** the provider issues a single `/route` POST per leg, mapping Drive→`auto`, Walk→`pedestrian`, Cycle→`bicycle` (FR-5, FR-6) against one configured base URL
**And** it parses `trip.summary.time` to int seconds and `trip.summary.length` km→×1000 meters at the provider edge only, returning Fidelity **Measured** with the encoded route geometry (FR-5, FR-7, NFR-11)
**And** geometry is treated as **precision 6** (polyline6), `ValhallaOptions.GeometryPrecision` defaults to `6`, and the `LeafletMap`/`IMapService` decoder is verified to decode Valhalla geometry at precision 6 so the map renders the polyline correctly (FR-7, AD-3)
**And** request coordinates are sent as `{lat, lon}` JSON formatted with `CultureInfo.InvariantCulture` (AD-3 provider-swap trap)
**And** `ProducesMeasuredFidelity` returns `true` and `Attribution` returns the Valhalla ODbL string (FR-10)
**And** Air/AnyAir legs return Placeholder **without** issuing an HTTP request (FR-9)
**And** a timeout, HTTP error, no-route response, or missing/blank geometry throws `ValhallaRouteUnavailableException` (AD-3 — a null-geometry Measured row must never persist)
**And** `ValhallaOptions` binds from `TravelTime:Valhalla` with defaults `BaseUrl=http://valhalla:8002`, `RequestTimeoutSeconds=10`, `GeometryPrecision=6`, using a named `"valhalla"` `IHttpClientFactory` client
**And** unit tests (mirroring `OsrmTravelTimeProviderTests`) cover the costing map, km→m and seconds conversion, precision-6 geometry, the Air-skips-HTTP path, and each failure→exception path.

### Story 2.3: Capability-gated recompute trigger + degrade

As a trip planner,
I want estimated legs to be upgraded to measured values once a measured provider becomes available, and any failing measured leg to fall back cleanly,
So that the trip view converges on the best available fidelity without ever failing the batch or downgrading good data.

**Acceptance Criteria:**

**Given** `TravelTimeComputationBackgroundService` today enqueues a leg iff no cache row exists
**When** I broaden the pending-leg predicate to "no row exists **OR** the row is upgrade-eligible", where upgrade-eligible = `Fidelity ∈ {Estimated, Placeholder}` **and** `Source ∈ {Mock, EstimatedFallback}` (AD-2)
**Then** the broadened arm is included **only when** the active provider's `ProducesMeasuredFidelity` is `true`, so a Mock deployment never re-churns its own estimates (AD-2)
**And** the upsert guard still returns early on `Fidelity is Manual or Measured`, so the broadened read never downgrades a protected row (`[TRIP-MANUAL-01]`, NFR-10)
**And** any Valhalla failure (including `ValhallaRouteUnavailableException`) degrades that leg to the smart-haversine estimate with `Source=EstimatedFallback`, one leg at a time, never failing the pass (FR-8, `[TRIP-DEGRADE-01]`)
**And** the leg **shape** (consecutive pairs + roundtrip closing leg) is unchanged, keeping the three-site projection mirror (`BuildLegs` / `DirectionalPairs` / MCP `GetTrip`) aligned (AD-2 mirror check)
**And** unit tests cover: upgrade-eligible row recomputed when provider is measured-capable, the same row left alone when provider is Mock, Manual/Measured rows never re-enqueued, and a failing leg degrading without aborting the batch.

### Story 2.4: Config/DI selection of the Valhalla provider

As a deployment operator,
I want to select Valhalla with a single config value,
So that I can switch the running deployment to measured routing without code changes.

**Acceptance Criteria:**

**Given** `AddTripServices(IConfiguration)` currently branches on `TravelTime:Provider=="Osrm"`
**When** I replace that branch with a `=="Valhalla"` branch that binds `TravelTime:Valhalla`, registers the named `"valhalla"` HttpClient, and registers `ValhallaTravelTimeProvider` as the active `ITravelTimeProvider` (AD-4)
**Then** the parameterless `AddTripServices()` overload still registers `MockTravelTimeProvider` (the smart-haversine default the integration host composes by hand) (NFR-13)
**And** with `TravelTime:Provider=Valhalla` set, the active provider is Valhalla and its ODbL attribution surfaces on the map via the existing `provider.Attribution → RoutingAttributionHtml → MapPage → LeafletMap` chain; with the default active, no routing attribution shows (FR-10, NFR8)
**And** a `TravelTime:Valhalla` section with documented defaults is added to `appsettings.json`
**And** the Trip integration test filter passes: `dotnet test --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"` (NFR-13, recurring regression point)
**And** the build is clean under the analyzer regime (NFR-12).

### Story 2.5: Turnkey docker-valhalla compose service + tile-build window degrade

As a deployment operator,
I want a single auto-building Valhalla container I enable with one profile and one env var, that degrades gracefully while it builds tiles,
So that I can reach measured routing without an ops project and without the trip view breaking during first boot.

**Acceptance Criteria:**

**Given** `docker-compose.yml` and the app's commented provider env block
**When** I add a single `valhalla` service under a `valhalla` compose profile that auto-downloads the region `.pbf` from `tile_urls` and auto-builds tiles into a mapped volume (`./appdata/valhalla:/custom_files`), exposing `8002`, with the image referenced by an immutable pin (specific tag, ideally `@sha256:` digest) and **never** `:latest` (FR-11, AD-8, OQ-7)
**Then** default `docker compose up` starts none of the Valhalla service (profile-gated), and enabling measured routing requires exactly: start the profile, set `tile_urls`, set `TravelTime__Provider=Valhalla` (FR-12) — no manual extract/partition/customize steps
**And** while Valhalla is building tiles (first start and every `.pbf` auto-rebuild) it is unreachable, so ground legs degrade to the smart-haversine estimate (Fidelity Estimated) rather than erroring or hanging the trip view (FR-13a, via Story 2.3 degrade path)
**And** the tile-build/unreachable condition is **operator-visible** via at least a clear startup/health log line stating tiles are building and routing is temporarily estimated (FR-13a)
**And** once Valhalla becomes reachable the background pass re-attempts and upgrades the Estimated/fallback legs to Measured (Story 2.3 trigger), never overwriting Manual/Measured (FR-13a)
**And** the commented app-env block is updated to the Valhalla form (`TravelTime__Provider`, `TravelTime__Valhalla__BaseUrl`).

### Story 2.6: NFR7 no-egress verification, operator doc, and fidelity-ladder confirmation

As a deployment operator,
I want proof and documentation that stop coordinates never leave my deployment at any rung, plus a coherent fidelity ladder,
So that I can trust the hard privacy guarantee and read each leg's honesty badge at a glance.

**Acceptance Criteria:**

**Given** the hard privacy constraint NFR7 and the existing two-badge model
**When** I add automated tests and operator documentation for the measured-routing setup
**Then** an automated test asserts the active default (smart-haversine) provider issues **no** outbound HTTP for a leg (computes in-process), and that the Valhalla provider contacts **only** the configured internal base-URL host (no other host) (NFR7, AD-5)
**And** a new operator document (replacing the role of `docs/osrm.md`) describes the turnkey setup, region selection, expected one-time tile-build cost (time/disk/RAM — measured during implementation, OQ-3/NFR-9), the privacy guarantee, and a documented operator check that no stop-coordinate egress occurs during normal routing (FR-13, NFR7, AD-5)
**And** the trip view presents the coherent fidelity ladder using the existing badges only — **Estimated** (smart-haversine default & fallback), **Measured** (Valhalla), **Manual**, **Placeholder/"—"** — with no new badge type introduced (FR-17)
**And** no Manual or Measured cache row is downgraded or deleted across the estimate→measured progression (NFR-10 counter-metric).

---

## Epic 3: Retire OSRM (removal & migration)

Remove the superseded hand-rolled OSRM path cleanly and safely now that Valhalla is the measured provider: a now-retired `TravelTime:Provider=Osrm` warns prominently and falls back to the smart-haversine default (never bricks boot), a one-time migration invalidates stale `Source=OSRM` cache rows so they recompute under the active provider (never touching Manual rows), and all hand-rolled OSRM artifacts are deleted. This epic follows Epic 2 (Valhalla must already be the replacement). Stories are independently completable; the FR-16 purge matches the literal string `"OSRM"` precisely so it does not depend on the constant being kept.

### Story 3.1: Prominent warn-and-fallback for retired/unknown provider ids

As a deployment operator upgrading from OSRM,
I want a stale `TravelTime:Provider=Osrm` (or any unknown value) to keep my app booting while loudly telling me my deployment downgraded to estimates,
So that a forgotten config never bricks the deployment but also never silently demotes me from Measured to Estimated unnoticed.

**Acceptance Criteria:**

**Given** the DI selection now recognizes only `Valhalla` (and the implicit default), per Epic 2 Story 2.4
**When** `TravelTime:Provider` is set to a retired/unknown value such as `Osrm`
**Then** the app falls back to the smart-haversine default and **does not** fail to boot (FR-15, AD-7 — warn+fallback, not fail-fast)
**And** a prominent high-level startup warning is emitted naming the offending value and stating that routing is now Estimated, not Measured (FR-15)
**And** the migration/release note calls out the breaking change and the warn+fallback behavior (FR-15, PRD §8)
**And** a unit/integration test asserts the warning is logged and the active provider is the default for an unknown id.

### Story 3.2: One-time invalidation of OSRM cache rows

As a deployment operator migrating to Valhalla,
I want my old OSRM-measured cache rows cleared once on startup,
So that stale, un-reproducible OSRM measurements are recomputed by the active provider instead of being pinned forever by the never-downgrade-Measured guard.

**Acceptance Criteria:**

**Given** existing `RouteSegment` rows with `Source="OSRM"` and Fidelity Measured, and the `[TRIP-MANUAL-01]` guard that would otherwise pin them
**When** a one-time startup migration runs in `Services/StartupCleanupService.cs`
**Then** every `RouteSegment` whose `Source` equals the **literal** string `"OSRM"` is deleted under `SqliteWriteLock`, with a code comment noting the literal is intentional because the constant is removed (FR-16, AD-6)
**And** `Manual` rows are never touched (FR-16, `[TRIP-MANUAL-01]`)
**And** the deleted-row count is logged, and the migration is idempotent/self-retiring (a no-op once the rows are gone) (AD-6)
**And** the invalidated legs are subsequently recomputed by the active provider via the existing missing-row trigger
**And** a test asserts OSRM rows are purged, Manual rows survive, and a second run is a no-op.

### Story 3.3: Delete all hand-rolled OSRM artifacts

As an implementing developer,
I want every hand-rolled OSRM artifact removed,
So that the codebase carries no dead routing path and the build stays clean.

**Acceptance Criteria:**

**Given** Valhalla is the active measured provider and the DI branch no longer references OSRM
**When** I remove the OSRM artifacts
**Then** the following are deleted: `OsrmTravelTimeProvider.cs`, `OsrmOptions.cs`, `OsrmRouteUnavailableException.cs`, the `TravelTimeSource.Osrm` constant, any residual named `"osrm"` HttpClient registration, the three `osrm-{car,foot,bike}` compose services + their commented env block, `OsrmTravelTimeProviderTests.cs`, the OSRM references in `TravelTimeComputationBackgroundServiceTests.cs`, and `docs/osrm.md` (FR-14)
**And** the now-unused OSRM attribution string (`TripRoutingAttributionOsm`) is removed from `UiStrings.cs` with no dangling reference (FR-14, AD-9)
**And** the solution compiles clean under `TreatWarningsAsErrors` with no group-B analyzer violations and no broken references (NFR-12)
**And** the full test suite (including the Trip integration filter) passes after removal.
