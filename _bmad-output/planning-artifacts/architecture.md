---
stepsCompleted: [1, 2, 3, 4, 5, 6, 7, 8]
lastStep: 8
status: 'complete'
completedAt: '2026-06-11'
inputDocuments:
  - _bmad-output/planning-artifacts/prds/prd-maps_editor-2026-06-11/prd.md
  - _bmad-output/planning-artifacts/prds/prd-maps_editor-2026-06-11/addendum.md
  - _bmad-output/planning-artifacts/ux-designs/ux-maps_editor-2026-06-11/DESIGN.md
  - _bmad-output/planning-artifacts/ux-designs/ux-maps_editor-2026-06-11/EXPERIENCE.md
  - docs/architecture.md
  - docs/data-models.md
  - docs/source-tree-analysis.md
  - docs/index.md
  - _bmad-output/project-context.md
workflowType: 'architecture'
project_name: 'maps_editor'
user_name: 'Yurik'
date: '2026-06-11'
---

# Architecture Decision Document

_This document builds collaboratively through step-by-step discovery. Sections are appended as we work through each architectural decision together._

## Project Context Analysis

### Requirements Overview

**Functional Requirements:** 17 FRs across 6 feature groups — Trip View toggle &
Stop ordering (FR-1–4, 17), route visualization on the map (FR-5–7), travel-time &
distance computation (FR-8–11), dwell time & itinerary timeline (FR-12–13),
Start/Finish & roundtrip (FR-14), and ordering paths — manual / TSP-Sort / MCP
(FR-15–16). Architecturally these collapse into five seams: (1) trip-shaped data on
existing Collection/membership entities; (2) a pluggable Travel-Time Provider
contract with self-describing Fidelity; (3) a per-pair Leg cache + on-demand N×N
Distance Matrix with invalidation and Estimated→Measured upgrade; (4) an in-process
NN+2-opt TSP-Sort; (5) Leaflet polyline rendering + MCP trip tools. Trip View is an
additive *lens* over an existing Collection — no new top-level entity.

**Non-Functional Requirements:** off-circuit background compute resolving via
`StateChanged` (mirrors `PoiEnrichmentBackgroundService`); incremental map redraw;
honest Fidelity badging (Measured/Estimated/Placeholder/Manual) with aggregate
"lowest-fidelity-wins" totals; privacy/egress guard (out-calling providers surface
consent before first call); OSM/ODbL attribution; dual desktop/`Mobile*Screen`
render paths; keyboard-accessible stop reorder; `aria-live` on compute states; all
UI text via `UiStrings`; TSP-Sort p95 ≤ 3s warm for N≤30; cache must keep
recomputation rare (SM-C2).

**Scale & Complexity:**
- Primary domain: full-stack web (Blazor Server monolith, .NET 8 / C# 14)
- Complexity level: Medium — bounded brownfield feature; hard parts are the provider
  abstraction, cache invalidation, and dual-surface UI, not scale/distribution
- Estimated architectural components: ~5 service-layer additions (provider abstraction
  + Mock impl, travel-time compute background service, Distance-Matrix/TSP service,
  trip ViewModel(s), MCP trip tools) + 1 schema migration + Leaflet interop extension

### Technical Constraints & Dependencies

- **Brownfield-first:** every addition must follow existing patterns —
  `IDbContextFactory<AppDbContext>`, `SqliteWriteLock` write serialization, Polly
  named pipelines, Coravel/Channel background queues, interface-first vertical slices,
  Transient ViewModels with `StateChanged`, composition-root DI in
  `Configuration/*Extensions.cs`. No group-B analyzer violations in new code;
  warnings-as-errors; no `ConfigureAwait(false)`.
- **Provider is config-selected per deployment;** v1 commits only the Mock default.
  OSRM (if chosen) is a docker-compose sidecar with region-scoped OSM extract — NOT a
  launch dependency.
- **Schema:** new migration via startup `MigrateAsync` (never EnsureCreated / never
  hand-edit applied migrations; SQLite limited ALTER). New fields on
  `PoiCollectionItem` and `PoiCollection`; new `RouteSegment` cache entity with
  `Version` concurrency token.
- **MCP trip tools** ride the existing three-tier `/mcp` auth (LAN → API key → OAuth).
- **Licensing/privacy:** OSM data is ODbL (UI attribution required); out-calling
  providers must surface data egress before first call; scrape provider carries ToS
  exposure (rejected as default).

### Cross-Cutting Concerns Identified

- **Travel-Time Provider abstraction + Fidelity** — single seam the whole feature
  depends on; Estimated is the universal fallback (FR-10).
- **Leg cache & invalidation** — keyed by `(FromPoi, ToPoi, TravelMode, Provider)`;
  feeds both displayed Legs and the Distance Matrix; invalidated on coord/mode/
  provider/assumed-speed change; Estimated→Measured upgrade is explicit, not silent.
- **Background compute + SQLite single-writer** — travel-time service mirrors
  enrichment (poll/trigger, per-worker DbContext, `SqliteWriteLock`), resolves via
  `StateChanged`.
- **Leaflet interop extension** — polyline rendering (straight + road geometry) in
  `leafletInterop.js` / `LeafletMap.razor` / `LeafletMapService.cs`; list↔map sync.
- **Dual-surface UI + accessibility** — desktop and `Mobile*Screen` both implement
  Trip View; keyboard reorder path required before ship; `UiStrings` for all copy.
- **MCP trip tools** — new authenticated tools, no new unauthenticated surface.

### Open Questions routed to this workflow
- OQ1 — recommended Measured provider default (OSRM vs scrape vs BYO-key vs Manual-only)
- OQ5 — OSRM deployment shape if chosen (instances vs profiles; OSM extract scope/RAM)
- OQ7 — Any/Air speed model (single assumed speed vs distance-tiered)

## Starter Template Evaluation

### Primary Technology Domain

Full-stack web — Blazor Server (.NET 8 / C# 14) monolith. **Brownfield**: an existing,
running application, not a new project.

### Starter Options Considered

**None — not applicable.** Starter-template evaluation is a greenfield concern. The Trip
Planning capability is an additive feature on the mature LucidCartographer codebase; the
PRD and UX explicitly require it to follow existing patterns. Adopting any starter would
mean discarding a working app and its conventions, directly contradicting the feature's
"additive lens, not a new thing" premise. No web research into starters was performed
because the stack is already committed and appropriate.

### Selected Foundation: Existing LucidCartographer codebase (no new starter)

**Rationale:** The established stack already satisfies every requirement this feature
imposes — server-driven interactive UI, background compute, a pluggable-provider
precedent (enrichment), an MCP slice, EF Core migrations, and a dual-surface UI system.
The architectural task is integration within these patterns, not foundation selection.

**Initialization Command:** N/A — no project scaffolding. The first implementation story
is instead the **EF Core migration** adding trip fields (see Technical Constraints), via
the existing startup `MigrateAsync` path.

**Architectural Decisions Already Fixed by the Existing Codebase:**

- **Language & Runtime:** C# 14 (`LangVersion 14.0`) on `net8.0`; .NET 10 SDK required to
  build (CS9202 on mismatch). `Nullable=enable`, `ImplicitUsings=enable`,
  `TreatWarningsAsErrors=true`, `EnforceCodeStyleInBuild=true`. Meziantou +
  VisualStudio.Threading analyzers; new code introduces no group-B violation; no
  `ConfigureAwait(false)` (Blazor circuit sync context).
- **Styling Solution:** Tailwind v3.4.17 (standalone CLI auto-downloaded into `obj/`, no
  Node) with the project's `surface-*` / `on-surface-*` / `primary` token palette; dual
  desktop/`Mobile*Screen` render paths.
- **Build Tooling:** MSBuild + `dotnet`; Docker multi-stage build (keep SDK + Tailwind
  versions in sync with `Directory.Build.props` and the app `.csproj`).
- **Testing Framework:** xUnit + FluentAssertions + Moq for unit; bUnit for component;
  `IntegrationTestBase` (real WebApplication + Playwright + per-test temp SQLite) +
  `MobileTestBase` for integration. `InternalsVisibleTo("LucidCartographer.Tests")`.
- **Code Organization:** Components (`.razor` + `*ViewModel.cs`) → ViewModels
  (Transient, `StateChanged`) → Services (interface-first vertical slices under
  `Services/<Slice>/`) → Data (EF Core via `IDbContextFactory`). DI in
  `Configuration/*Extensions.cs`; endpoints in `Endpoints/*Endpoints.cs`; one-shot
  startup in `Services/StartupCleanupService.cs`.
- **Development Experience:** `dotnet run --project LucidCartographer` or
  `docker-compose up`; `dotnet test`; admin password seeded to first-run log.

**Note:** Because there is no scaffolding step, the first implementation story is the
EF Core migration for trip fields, not a project-init command.

## Core Architectural Decisions

### Decision Priority Analysis

**Critical Decisions (Block Implementation):**
- D1 Schema/data model for trip semantics (new fields + RouteSegment cache entity)
- D2 Travel-Time Provider contract + Mock default; OSRM as recommended Measured provider (OQ1)
- D3 Leg cache + invalidation + Estimated→Measured upgrade
- D4 Background travel-time compute service (mirrors enrichment)
- D5 TSP-Sort algorithm (in-process NN + 2-opt)
- D6 Map leg rendering approach (Leaflet Routing Machine, via custom IRouter)
- D7 MCP trip tools on existing authenticated /mcp slice
- D8 Keyboard-accessible stop reorder (move up/down buttons) — a11y build-blocker

**Important Decisions (Shape Architecture):**
- D9 Any/Air Estimated speed model — single configurable speed (OQ7)
- D10 TripViewEnabled persistence — per-Collection (confirms PRD OQ8 / UX)
- D11 Distance Matrix computation strategy (on-demand, shares the Leg cache)

**Deferred Decisions (Post-MVP / not launch-blocking):**
- OSRM deployment shape detail (OQ5) — recommended default documented (D2a); fully
  deferrable because OSRM is not a launch dependency (Mock ships).
- Per-Leg Travel-Mode override (mixed-mode trips) — PRD §6.2 deferral; revisit post-v1.
- Provider data-refresh automation; hosted/BYO-key provider impl; opening-hours hard
  scheduling — all PRD non-goals/deferrals.

### Data Architecture

**D1 — Trip schema (EF Core migration via startup `MigrateAsync`):**
- `PoiCollectionItem.OrderIndex` (int) — Stop Order within the Trip. Seeded by
  `AddedDate` ascending on first Trip-View open; contiguous, gap-free, unique per Trip.
- `PoiCollectionItem.DwellMinutes` (int?, nullable) — per-Stop dwell; lives on the join
  (not on Poi) so the same POI carries different dwell across Trips.
- `PoiCollection` trip fields — `TravelMode` (enum: AnyAir/Drive/Walk/Cycle),
  `StartPoiId` (nullable FK), `FinishPoiId` (nullable FK; null ⇒ roundtrip),
  `TripStartTime` (nullable), `TimeBudgetMinutes` (int?, nullable soft flag),
  `TripViewEnabled` (bool, per-Collection — D10).
- **New entity `RouteSegment`** (Leg cache) — key `(FromPoiId, ToPoiId, TravelMode)`;
  columns `DurationSeconds`, `DistanceMeters`, `GeometryPolyline` (nullable),
  `Fidelity` (enum), `Source`/`Provider`, `ComputedAt`, `Version` (concurrency token).
- Conventions: `IDbContextFactory<AppDbContext>`, Fluent API + check constraints,
  `Version` optimistic concurrency, indexes on the cache key and FK columns. New
  migration only (never EnsureCreated; never hand-edit an applied migration).

**D11 — Distance Matrix:** computed on demand (input to TSP-Sort), N×N over placeable
Stops; reads/writes the **same** `RouteSegment` cache as the displayed Legs — one cache,
two readers. No separate matrix table.

### Authentication & Security

No new auth surface. **D7** MCP trip tools ride the existing three-tier `/mcp` guard
(LAN → API key → OAuth). **Privacy/egress guard (firm):** any out-calling provider
(BYO-key, scrape) must surface explicit operator consent before its first out-call;
Mock and self-hosted OSRM keep coordinates in-deployment. **Licensing:** OSM data is
ODbL → OSM attribution rendered on the map on both surfaces when an OSM-based provider
is active.

### API & Communication Patterns

**D2 — Travel-Time Provider abstraction (the central seam):**
`ITravelTimeProvider.GetLegAsync(fromStop, toStop, travelMode) ->
(duration, distance, Fidelity, geometry?)`, Fidelity ∈ {Measured, Estimated,
Placeholder, Manual}. Config-selected per deployment; one active provider with the
haversine **Mock (Estimated)** as the universal fallback (FR-10).
- **Mock (haversine × assumed speed)** — shipping default, zero infra (Estimated).
- **OSRM (recommended Measured provider, OQ1)** — built in v1 as the reference
  Measured impl: queries OSRM `/table` (matrix) and `/route` (`geometries=geojson`),
  yields Measured + geometry. BSD-2 engine code; ODbL data. Optional, NOT a launch
  dependency.
- **Manual** — user-entered per-Leg time (Manual fidelity), already required for Air.
- **BYO Google Routes** — documented as a future opt-in metered provider; not built v1.

**D2a — OSRM deployment shape (OQ5, recommended default, deferrable):** region-scoped
OSM extract (not global) to bound image/RAM; one OSRM container **per ground profile**
(car/bike/foot) in docker-compose under an optional compose profile (OSRM preprocesses
data per profile, so profiles ≠ runtime switch). Walk/Cycle/Drive map to foot/bike/car.
Started only when the operator opts into OSRM; Mock needs none of this. Image:
`ghcr.io/project-osrm/osrm-backend` (version-pinned).

**D9 — Any/Air speed model (OQ7):** a single configurable assumed speed applied to all
Any/Air legs, surfaced as a badged Placeholder/Estimated; Manual per-Leg entry
overrides it. (Distance-tiered speeds rejected as over-engineering for a badged guess.)

### Frontend Architecture

**D4 — Background travel-time compute:** a `TravelTimeComputationBackgroundService`
mirroring `PoiEnrichmentBackgroundService` — poll/trigger (`TravelTimeTrigger`),
per-worker DbContext, `SqliteWriteLock` for cache writes, Polly-wrapped provider calls.
Resolves to the UI via the ViewModel's `StateChanged`; UI shows pending/computing state
via `aria-live`. Long compute never runs on the circuit thread.

**D3 — Cache invalidation + upgrade:** a `RouteSegment` entry is invalidated when any of
its inputs change — either endpoint's coordinates, the Trip's TravelMode, the active
Provider, or the Any/Air assumed-speed setting. A Stop Order change that introduces no
new `(From,To,Mode)` pair triggers NO recompute (only redrawn Legs) — protects SM-C2.
**Estimated→Measured upgrade** is explicit, never silent: fires on an operator
"Recompute travel times" action and/or a provider-available signal; lands via
`StateChanged`.

**D5 — TSP-Sort:** in-process C# (~150 lines), no OR-Tools. Build duration matrix from
the cache → nearest-neighbor construction → 2-opt local search; pin Start (order 1) and
Finish (order N), swap interior edges only; close the loop for a roundtrip. Result ≤
pre-sort total; rewrites `OrderIndex`; overridable by manual drag. Target N≤30, p95 ≤ 3s
warm (SM-5).

**D6 — Map leg rendering — Leaflet Routing Machine (user decision, with caveats):**
- LRM adopted for line rendering + waypoint-drag, **version-pinned**.
- **Risk recorded:** LRM is effectively unmaintained (no npm release in 12+ months;
  its default OSRM demo backend is dead). Accepted by the maintainer.
- **Integration constraint (important):** legs are computed server-side via
  `ITravelTimeProvider` and cached (`RouteSegment`). To avoid LRM independently calling
  OSRM and bypassing our cache/Fidelity, we implement a **custom LRM `IRouter`** that
  returns our already-computed cached legs+geometry. LRM is a rendering/interaction
  widget only; the server-side cache stays the single source of truth.
- The `IRouter` seam is kept thin so a later swap to a custom `L.polyline` interop
  layer requires no data-layer change. Phase 1 = straight connectors; Phase 2 = road
  geometry from OSRM. Air/Any legs render as dashed great-circle lines; only Measured
  legs render solid (geometric-fidelity rule from DESIGN.md). list↔map two-way sync
  reuses existing marker-click interop.

**D8 — Keyboard-accessible stop reorder (a11y build-blocker):** keyboard-focusable
move-up / move-down controls per Stop row, `aria-label`led, announced via `aria-live`;
implemented identically on desktop and `Mobile*Screen`. Drag remains the pointer path.

**D10 — Trip View persistence:** per-Collection (persisted on `PoiCollection`), confirming
PRD OQ8 and the UX spec. Reopening a Collection restores Trip on/off + Stop Order.

Trip state lives in a sealed, Transient `TripViewModel` (per project layering) exposing
`StateChanged`; the existing map/collection page composes it. All new UI text via
`UiStrings`; both render paths implemented.

### Infrastructure & Deployment

No change to the core deployment (single Blazor Server container + SQLite volume). OSRM,
if enabled, is an **optional docker-compose sidecar** (D2a) — region extract, per-profile
container(s), behind an opt-in compose profile. OSM data staleness handled by manual
refresh in v1 (automation deferred). No CI/monitoring changes beyond logging
travel-time computations and provider failures, distinguishing Measured vs
Estimated/Placeholder/Manual legs (feeds SM-3 / observability NFR).

### Decision Impact Analysis

**Implementation Sequence (suggested):**
1. D1 schema migration (the only unavoidable migration) → first story.
2. D2 provider contract + Mock impl (unblocks everything; ships v1 with no infra).
3. D3 RouteSegment cache + invalidation; D11 distance matrix over the cache.
4. D4 background compute service + `StateChanged` wiring.
5. D10/Trip ViewModel + Trip View toggle (FR-1/2/4/17); D8 keyboard reorder.
6. D6 map rendering (LRM IRouter, straight Phase 1) + list↔map sync.
7. D5 TSP-Sort; D9 Any/Air speed; timeline (FR-12/13); Start/Finish (FR-14).
8. D7 MCP trip tools.
9. (Optional/deferrable) D2 OSRM adapter + D2a sidecar → Phase 2 road geometry.

**Cross-Component Dependencies:**
- D2 (provider contract) gates D3/D4/D5/D6 — everything reads `(duration, distance,
  Fidelity, geometry?)` from it.
- D3 cache is shared by displayed Legs (D6) and the Distance Matrix (D11/D5) — one
  invalidation policy serves both.
- D6's LRM `IRouter` depends on D3 (returns cached legs), not on OSRM directly.
- D8 (keyboard reorder) and drag both write `OrderIndex` (D1) and trigger the same
  redraw+recompute path as TSP/MCP ordering — one ordering write-path, four triggers.

## Implementation Patterns & Consistency Rules

> Generic conventions (table/column naming, DI lifetimes, layering, error/loading
> handling, no-hardcoded-text, analyzer discipline) are already fixed by
> `project-context.md` and the existing codebase — agents inherit them unchanged.
> This section pins only the **feature-specific** patterns where Trip-Planning agents
> could otherwise diverge.

### Critical Conflict Points Identified
9 feature-specific areas where independent AI agents could make incompatible choices.

### Naming Patterns

**Data / enum naming:**
- Enums `TravelMode` (AnyAir, Drive, Walk, Cycle) and `Fidelity` (Measured, Estimated,
  Placeholder, Manual) are **persisted as strings** with an EF check constraint,
  matching the existing `PoiCategory` constant precedent (queryable, migration-safe,
  human-readable in the DB). NOT int-backed.
- New columns follow existing casing: PascalCase C# properties; EF default column
  names. `OrderIndex`, `DwellMinutes`, `TravelMode`, `StartPoiId`, `FinishPoiId`,
  `TripStartTime`, `TimeBudgetMinutes`, `TripViewEnabled`.
- `RouteSegment` columns: `FromPoiId`, `ToPoiId`, `TravelMode`, `DurationSeconds`,
  `DistanceMeters`, `GeometryPolyline`, `Fidelity`, `Source`, `ComputedAt`, `Version`.

**Trigger / service naming:** mirror the enrichment/dedup precedent —
`TravelTimeTrigger` (event signal), `TravelTimeComputationBackgroundService`
(`BackgroundService`), `ITravelTimeProvider` + impls `HaversineMockTravelTimeProvider`,
`OsrmTravelTimeProvider`. TSP lives in `TripOrderingService` / `ITripOrderingService`.

**MCP tool naming:** new `TripTools` class in `Services/Mcp/` alongside
`PoiReadTools`/`PoiWriteTools`/`EnrichmentTools`; tool methods verb-first
(e.g. `GetTripStops`, `AssignStopOrder`, `SetStartFinish`, `SetDwellTime`).

**JS interop naming:** camelCase verb functions added to `leafletInterop.js`
(e.g. `drawTripLegs`, `clearTripLegs`, `highlightStop`); invoked via
`LeafletMapService.cs`. No new JS module — extend the existing one.

### Structure Patterns

- New vertical slice **`Services/Trip/`**, interface-first, holding: the provider
  abstraction + impls (`Services/Trip/Providers/`), `TripOrderingService` (TSP),
  `DistanceMatrixService`, `ItineraryTimelineService`, the background compute service,
  and `TravelTimeTrigger`. Trip MCP tools stay in `Services/Mcp/` (existing slice).
- `TripViewModel` (sealed, Transient, primary-ctor DI, `StateChanged` + `Notify()`,
  `IAsyncDisposable`) registered in `Configuration/ViewModelExtensions.cs`; trip DI
  registration in a new `Configuration/TripServicesExtensions.cs`.
- Trip UI: Trip View components under `Components/Shared/Trip/`, with a
  desktop + `MobileTrip*` split per the dual-render-path rule.
- Tests follow the three existing layers; trip unit tests under `Services/` &
  `ViewModels/`, component (bUnit) tests, and Mobile/desktop integration coverage.

### Format Patterns

- **Canonical units (non-negotiable):** durations stored in **seconds**
  (`DurationSeconds`), distances in **meters** (`DistanceMeters`), user-facing dwell &
  budget in **minutes** (`DwellMinutes`, `TimeBudgetMinutes`). Convert at the UI edge
  only; never mix units across a layer boundary.
- **`OrderIndex` is 1-based** (Stop Order 1..N, contiguous, gap-free) — stored exactly
  as displayed; no 0-based storage + display offset. Start = OrderIndex 1, Finish =
  OrderIndex N.
- **Directed cache key:** `(FromPoiId, ToPoiId, TravelMode)` is **directional** — A→B
  and B→A are distinct `RouteSegment` rows. Never collapse the pair order.
- Geometry stored as an encoded polyline string (`GeometryPolyline`, nullable; null =
  no road geometry → dashed/muted render). Request OSRM with `geometries=geojson`
  and encode for storage, or store the polyline form consistently — one encoding
  project-wide.
- Fidelity is authoritative on every leg value; UI badges read it directly. Aggregate
  totals inherit the **lowest** fidelity among summed legs (DESIGN.md honesty rule).

### Communication Patterns

- Background→UI updates flow through the ViewModel's `event Action? StateChanged`
  marshalled via `InvokeAsync(StateHasChanged)` — never direct component mutation,
  never polling. Compute runs off the circuit thread; results land via `StateChanged`.
- Cache writes go through `SqliteWriteLock` (single-writer), same as enrichment/dedup.
- The four ordering paths (drag, keyboard move, TSP-Sort, MCP) all write the **same**
  `OrderIndex` through one `TripOrderingService` method — no path mutates order rows
  directly. After any order write, the same redraw+recompute path runs.

### Process Patterns

- **Graceful degradation:** provider failure/out-of-coverage → fall back to the
  haversine Estimated provider; never blank a leg, never throw to the UI (FR-10).
- **Cache invalidation** is centralized in one service method keyed on the documented
  inputs (coords / mode / provider / assumed-speed); a no-op reorder triggers no
  recompute (SM-C2).
- **Egress guard:** an out-calling provider must pass an explicit operator-consent
  check before its first out-call; enforced in the provider-selection path, not in UI.

### Enforcement Guidelines

**All AI agents MUST:**
- Use the canonical units (s / m / minutes) and 1-based `OrderIndex`; treat the cache
  key as directional.
- Persist `TravelMode`/`Fidelity` as strings with check constraints; add schema only
  via a new EF migration applied through startup `MigrateAsync`.
- Route every order change through `TripOrderingService`; never bypass
  `ITravelTimeProvider` to call a routing engine directly (incl. from the LRM IRouter).
- Tag new trip design decisions with a searchable **`TRIP-*`** comment code
  (e.g. `TRIP-CACHE-01`, `TRIP-ORDER-01`), consistent with the existing
  `ARCH-*`/`IE-*`/`OPS-*` code convention.
- Implement every Trip affordance on **both** desktop and `Mobile*Screen` paths and
  route all copy through `UiStrings`.

**Pattern Enforcement:** warnings-as-errors + analyzers catch style; unit tests assert
unit conventions and 1-based ordering; integration tests cover both render paths;
`TRIP-*` codes make decisions greppable. Pattern changes are amended here deliberately.

### Pattern Examples

**Good:** `routeSegment.DurationSeconds` summed → converted to minutes once at the
timeline UI edge; `AssignStopOrder` MCP tool calls `TripOrderingService.SetOrder(...)`
which writes 1-based `OrderIndex` under `SqliteWriteLock` and raises `TravelTimeTrigger`.

**Anti-patterns:** storing dwell in seconds in one service and minutes in another;
0-based `OrderIndex` with `+1` in the view; the LRM IRouter fetching from OSRM directly
(bypassing the cache/Fidelity); a second JS module duplicating `leafletInterop` polyline
logic; int-backed enums diverging from the `PoiCategory` string-constant precedent.

## Project Structure & Boundaries

> Brownfield delta only. `[NEW]` = file/dir to create; `[MOD]` = existing file to
> extend. Everything else follows the existing source tree (see
> `docs/source-tree-analysis.md`). No files are moved or renamed.

### Complete Project Directory Structure (additions & modifications)

```
LucidCartographer/
├── Data/
│   ├── AppDbContext.cs                         [MOD] Fluent config + check constraints +
│   │                                                 indexes for new fields & RouteSegment
│   └── Entities/
│       ├── PoiCollection.cs                    [MOD] TravelMode, StartPoiId, FinishPoiId,
│       │                                              TripStartTime, TimeBudgetMinutes,
│       │                                              TripViewEnabled
│       ├── PoiCollectionItem.cs                [MOD] OrderIndex (1-based), DwellMinutes
│       ├── RouteSegment.cs                     [NEW] leg cache entity (+ Version token)
│       ├── TravelMode.cs                       [NEW] enum (string-persisted)
│       └── Fidelity.cs                         [NEW] enum (string-persisted)
├── Migrations/
│   └── <ts>_AddTripPlanning.cs                 [NEW] single migration (startup MigrateAsync)
│
├── Services/
│   └── Trip/                                   [NEW] vertical slice (interface-first)
│       ├── ITravelTimeProvider.cs              [NEW] (duration, distance, Fidelity, geometry?)
│       ├── Providers/
│       │   ├── HaversineMockTravelTimeProvider.cs   [NEW] shipping default (Estimated)
│       │   ├── OsrmTravelTimeProvider.cs            [NEW] Measured + geometry (optional)
│       │   └── ManualTravelTimeProvider.cs          [NEW] user-entered (Manual)
│       ├── ITripOrderingService.cs / TripOrderingService.cs   [NEW] NN+2-opt TSP, OrderIndex writes
│       ├── IDistanceMatrixService.cs / DistanceMatrixService.cs [NEW] on-demand N×N over cache
│       ├── IItineraryTimelineService.cs / ItineraryTimelineService.cs [NEW] FR-13 walk
│       ├── ITravelTimeCache.cs / RouteSegmentCacheService.cs   [NEW] read/invalidate/upgrade
│       ├── TravelTimeComputationBackgroundService.cs [NEW] mirrors enrichment service
│       └── TravelTimeTrigger.cs                [NEW] event signal (mirrors EnrichmentTrigger)
│   ├── Mcp/
│   │   └── TripTools.cs                         [NEW] GetTripStops, AssignStopOrder,
│   │                                                  SetStartFinish, SetDwellTime
│   └── LeafletMapService.cs                     [MOD] drawTripLegs/clearTripLegs/highlightStop interop
│
├── Configuration/
│   ├── TripServicesExtensions.cs               [NEW] DI for Trip slice + provider selection
│   └── ViewModelExtensions.cs                  [MOD] register TripViewModel (Transient)
│
├── Components/
│   ├── Pages/
│   │   └── (map/collection page)               [MOD] compose TripViewModel + toggle host
│   └── Shared/
│       └── Trip/                               [NEW]
│           ├── TripViewModel.cs                [NEW] sealed, Transient, StateChanged
│           ├── TripPanel.razor                 [NEW] desktop stop list / timeline panel
│           ├── TripViewToggle.razor            [NEW] filtered-results-region toggle
│           ├── StopListRow.razor               [NEW] order badge, dwell, move up/down, timeline
│           ├── TravelModeSelector.razor        [NEW] segmented control
│           ├── FidelityBadge.razor             [NEW] Measured/Estimated/Manual; "—" for unmeasured
│           ├── ItineraryTimeline.razor         [NEW] offsets + wall-clock + aggregate honesty
│           └── MobileTripPanel.razor           [NEW] Mobile*Screen render path
│
└── wwwroot/
    ├── js/leafletInterop.js                    [MOD] trip leg polylines (straight + geometry),
    │                                                 LRM custom IRouter, highlightStop, list↔map sync
    └── css/ (Tailwind input)                   [MOD] trip leg/badge styles (existing token palette)

LucidCartographer.Tests/
├── Services/Trip*Tests.cs                      [NEW] provider, TSP, matrix, timeline, cache
├── ViewModels/TripViewModelTests.cs            [NEW]
├── Components/Trip*Tests.cs                    [NEW] bUnit (toggle, stop list, badge, timeline)
└── Integration/ (+ Mobile)                     [NEW] Trip View flows, both render paths
```

### Architectural Boundaries

**API / external boundaries:** No new HTTP endpoints. The only external surface is the
existing `/mcp` server, extended with `TripTools` behind the unchanged three-tier auth
guard. OSRM (if enabled) is reached **only** from `OsrmTravelTimeProvider` — no other
code calls a routing engine; the LRM client-side IRouter calls back into the app for
cached legs, never OSRM directly.

**Component boundaries:** Components are thin bridges; all trip state and orchestration
live in `TripViewModel`, which calls the `Services/Trip/` interfaces. Map rendering is
mediated by `LeafletMapService` → `leafletInterop.js`; components never invoke JS
directly. Desktop and `MobileTripPanel` share the same ViewModel.

**Service boundaries:** `Services/Trip/` is interface-first and self-contained. The
provider abstraction (`ITravelTimeProvider`) is the sole gateway to travel data; the
ordering service (`ITripOrderingService`) is the sole writer of `OrderIndex`; the cache
service is the sole reader/invalidator of `RouteSegment`. Background compute talks to
providers + cache only, never to ViewModels (it signals via `TravelTimeTrigger` →
`StateChanged`).

**Data boundaries:** New schema is additive on `PoiCollection` / `PoiCollectionItem`
plus the `RouteSegment` cache. All access via `IDbContextFactory<AppDbContext>`; all
writes serialized by `SqliteWriteLock`. Cache rows are derived/disposable — invalidated
on coord/mode/provider/assumed-speed change; never the source of truth for trip intent.

### Requirements to Structure Mapping

| FR group | Lives in |
|---|---|
| FR-1,2,4,17 Trip View toggle, Stop Order, Unplaceable | `TripViewToggle.razor`, `TripViewModel`, `TripOrderingService`, schema (`OrderIndex`, `TripViewEnabled`) |
| FR-3,8 Reorder + Travel Mode | `StopListRow.razor`, `TravelModeSelector.razor`, `TripOrderingService`, `TripViewModel` |
| FR-5,6,7 Map legs, geometry, list↔map sync | `leafletInterop.js`, `LeafletMapService`, LRM IRouter, `RouteSegment.GeometryPolyline` |
| FR-9,10,11 Provider, degradation, cache | `Services/Trip/Providers/*`, `RouteSegmentCacheService`, `TravelTimeComputationBackgroundService` |
| FR-12,13 Dwell + Timeline | `DwellMinutes`, `ItineraryTimelineService`, `ItineraryTimeline.razor` |
| FR-14 Start/Finish/Roundtrip | `StartPoiId`/`FinishPoiId`, `TripOrderingService` pinning |
| FR-15 TSP-Sort | `TripOrderingService` (NN+2-opt), `DistanceMatrixService` |
| FR-16 MCP ordering | `Services/Mcp/TripTools.cs` |

**Cross-cutting:** Fidelity badging → `FidelityBadge.razor` + `Fidelity` on every leg;
egress guard → provider-selection path in `TripServicesExtensions`; a11y keyboard
reorder → move up/down controls in `StopListRow.razor` (+ Mobile); all copy → `UiStrings`.

### Integration Points

**Internal communication:** `StateChanged`/`InvokeAsync(StateHasChanged)` for VM→UI;
`TravelTimeTrigger` for compute wakeups; `SqliteWriteLock` for write serialization —
all mirroring the enrichment subsystem.

**External integrations:** OSRM sidecar (optional, `ghcr.io/project-osrm/osrm-backend`,
version-pinned) via `OsrmTravelTimeProvider`; LRM (pinned) client-side for rendering.
No metered/out-calling provider in v1.

**Data flow:** order change (drag/keyboard/TSP/MCP) → `TripOrderingService` writes
1-based `OrderIndex` under lock → `TravelTimeTrigger` → background service computes/looks
up legs via active provider, falls back to haversine on failure, writes `RouteSegment`
cache → `StateChanged` → VM recomputes timeline (lowest-fidelity aggregate) → component
redraws map legs incrementally + updates stop list.

### File Organization & Workflow

- **Config:** trip DI in `Configuration/TripServicesExtensions.cs` (called from
  `Program.cs` composition root); provider choice via existing config (`Database:`-style
  keys), egress consent gated there.
- **Source:** new logic confined to `Services/Trip/` + `Components/Shared/Trip/`; no
  changes to unrelated slices.
- **Tests:** the three existing layers, both render paths; unit tests assert canonical
  units and 1-based ordering.
- **Build/deploy:** unchanged for the default (Mock) deployment; OSRM is an opt-in
  docker-compose profile (region extract, per-profile container) — not a launch dep.

## Architecture Validation Results

### Coherence Validation ✅

**Decision Compatibility:** All choices compose cleanly. The provider contract (D2) is
the single dependency for cache (D3), compute (D4), TSP (D5), and rendering (D6); the
Mock default means v1 ships with zero new infra while OSRM/LRM remain optional. No
contradictory decisions. The one accepted risk — LRM (unmaintained) — is contained
behind a thin custom `IRouter` so it can be swapped without data-layer change.

**Pattern Consistency:** Naming, units, and triggers mirror the existing enrichment/dedup
subsystems (`*BackgroundService`, `*Trigger`, `SqliteWriteLock`, `StateChanged`).
String-persisted enums match the `PoiCategory` precedent. 1-based `OrderIndex` and
canonical units (s/m/min) are pinned and test-enforced.

**Structure Alignment:** The `Services/Trip/` slice + `Components/Shared/Trip/` honor the
Component→VM→Service→Data layering and interface-first convention. Boundaries are clean:
one writer of `OrderIndex`, one gateway to travel data, one cache owner. No unrelated
slice is touched.

### Requirements Coverage Validation ✅

**Functional Requirements Coverage (17/17):**
- FR-1/2/4/17 → toggle, seed/persist OrderIndex, Unplaceable, discoverable placement ✅
- FR-3/8 → drag + keyboard reorder, per-Trip Travel Mode + Air Manual slice ✅
- FR-5/6/7 → ordered legs (+ closing roundtrip leg), road geometry when Measured,
  list↔map sync ✅
- FR-9/10/11 → provider contract, haversine degradation, directed cache + explicit
  Estimated→Measured upgrade ✅
- FR-12/13 → Dwell on membership, timeline with Start-dwell-once / roundtrip return /
  Unplaceable-dwell / Placeholder propagation / soft budget overrun ✅
- FR-14 → Start (order 1) / Finish (order N) pinning, roundtrip default ✅
- FR-15 → on-demand NN+2-opt, pinned endpoints, ≤ pre-sort total, N≤30 p95 ≤ 3s ✅
- FR-16 → MCP TripTools on existing authed /mcp, order persists like a drag ✅

**Non-Functional Coverage:** Performance (warm-cache matrix, incremental redraw) ✅;
reliability/degradation (haversine fallback never blanks a leg) ✅; off-circuit compute
via StateChanged ✅; accessibility (keyboard reorder via move up/down, aria-live/labels,
both surfaces) ✅; i18n (UiStrings) ✅; observability (log Measured vs others) ✅;
privacy/egress guard + ODbL attribution ✅.

### Implementation Readiness Validation ✅

**Decision Completeness:** All critical decisions (D1–D8) documented with the OQ1/OQ5/OQ7
resolutions and versions (OSRM image, LRM pinned). **Structure Completeness:** every new
/modified file enumerated and FR-mapped. **Pattern Completeness:** the 9 feature-specific
conflict points pinned with examples and anti-patterns.

### Gap Analysis Results

**Critical Gaps:** None — nothing blocks implementation.

**Important Gaps (resolve at story time, not architecture time):**
- **Drag-reorder interop mechanism** (HTML5 DnD vs a small JS helper) is unspecified;
  the keyboard path (D8) is the a11y-safe baseline, so drag detail is non-blocking.
- **Coordinate-change → cache-invalidation hook:** the invalidation *policy* is defined,
  but the exact trigger when a POI's lat/lon changes (e.g. via enrichment) needs a
  concrete hook into the existing enrichment/save path during the cache story.
- **Geometry encoding:** "one encoding project-wide" is mandated but the concrete choice
  (store encoded polyline vs GeoJSON) is left to the Phase-2/OSRM story.

**Nice-to-Have Gaps:**
- `TripStartTime` timezone handling (UTC vs local display) — UX/story detail.
- A future swap-out plan for LRM → custom polyline layer if maintenance bites.

### Validation Issues Addressed
The LRM maintenance risk (the only notable concern) is mitigated architecturally by the
custom-`IRouter` boundary keeping the server-side cache authoritative and the swap path
cheap. No other issues required resolution.

### Architecture Completeness Checklist

**Requirements Analysis**
- [x] Project context thoroughly analyzed
- [x] Scale and complexity assessed
- [x] Technical constraints identified
- [x] Cross-cutting concerns mapped

**Architectural Decisions**
- [x] Critical decisions documented with versions
- [x] Technology stack fully specified
- [x] Integration patterns defined
- [x] Performance considerations addressed

**Implementation Patterns**
- [x] Naming conventions established
- [x] Structure patterns defined
- [x] Communication patterns specified
- [x] Process patterns documented

**Project Structure**
- [x] Complete directory structure defined
- [x] Component boundaries established
- [x] Integration points mapped
- [x] Requirements to structure mapping complete

### Architecture Readiness Assessment

**Overall Status:** READY FOR IMPLEMENTATION (all 16 checklist items confirmed; no
Critical Gaps — the open items are story-time implementation details, not architectural
decisions).

**Confidence Level:** High — the feature lands almost entirely on proven, existing
patterns; the genuinely novel parts (provider abstraction, directed cache, dual-surface
trip UI) are bounded and well-specified.

**Key Strengths:**
- Ships v1 with zero new infrastructure (Mock default); Measured routing is purely opt-in.
- Single provider seam + single cache owner + single OrderIndex writer = low conflict
  surface for parallel AI-agent work.
- Honesty/Fidelity model is carried end-to-end (data → cache → timeline aggregate → UI).
- Pure additive schema and slice; no risk to existing POI/enrichment behavior.

**Areas for Future Enhancement:**
- Replace LRM with a thin custom polyline layer if its unmaintained status causes friction.
- OSRM as recommended Measured provider (Phase 2); BYO-key hosted provider (opt-in, later).
- Per-Leg Travel-Mode override (mixed-mode trips) — revisit immediately post-v1 (UJ-3).

### Implementation Handoff

**AI Agent Guidelines:**
- Follow the decisions (D1–D11) and patterns exactly; respect the `Services/Trip/`
  boundaries and the canonical units / 1-based OrderIndex / directed cache key.
- Route all ordering through `TripOrderingService`; never call a routing engine outside
  `ITravelTimeProvider`; implement both render paths; all copy via `UiStrings`.
- Tag trip design decisions with `TRIP-*` comment codes.

**First Implementation Priority:** the `AddTripPlanning` EF Core migration (D1), then the
provider contract + Haversine Mock (D2) — together these unblock every subsequent story
and let Trip View ship without any routing infrastructure.
