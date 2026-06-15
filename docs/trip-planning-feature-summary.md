# Feature Summary — Trip Planning for Collections

**Status:** Shipped · **Delivered:** 2026-06-11 → 2026-06-14 (Epics 1–4) · **Scope:** all 17 functional requirements (FR-1…FR-17)

This is the milestone summary for the **Trip Planning** capability of LucidCartographer. It distills what shipped, the decisions that shaped it, how the system changed, and the lessons from four epic retrospectives. It is a summary, not a substitute — see the linked sources for detail.

**Sources:** [PRD](../_bmad-output/archive/trip-planning/planning-artifacts/prds/prd-maps_editor-2026-06-11/prd.md) · [Architecture](../_bmad-output/archive/trip-planning/planning-artifacts/architecture.md) · [Epics & Stories](../_bmad-output/archive/trip-planning/planning-artifacts/epics.md) · as-built reference [`trip-planning.md`](trip-planning.md) · operator guide [`osrm.md`](osrm.md) · retrospectives (epic-1…4-retro, archived under `_bmad-output/archive/trip-planning/implementation-artifacts/`).

> Note: planning/implementation artifacts were archived during finalization; the source links above point at their post-archive `_bmad-output/archive/trip-planning/…` location.

---

## What shipped

Trip Planning is an **additive lens over an existing POI Collection** — no new top-level entity. Toggle Trip View on and a plain collection becomes an ordered, mapped, timed trip; toggle it off and the plain collection returns untouched. It was delivered in four epics:

- **Epic 1 — Trip View (spatial backbone).** Toggle Trip View per-collection; deterministic seed Stop Order (1..N, contiguous); numbered stop badges on list + map markers; straight connecting legs (incl. the roundtrip closing leg); drag **and** keyboard reorder (a11y); Start/Finish designation with roundtrip default; unplaceable POIs flagged and excluded from routing but kept in the collection; two-way list↔map selection sync. One EF migration (`AddTripPlanning`) laid the entire persistent shape up front.
- **Epic 2 — Travel times, dwell & the honest itinerary timeline.** A pluggable `ITravelTimeProvider` (haversine **Mock** as the shipping default); per-leg travel time + distance + **Fidelity** badge (Measured/Estimated/Placeholder/Manual, em-dash for unmeasured); travel-mode selector (Any/Air · Drive · Walk · Cycle) with a Manual time for Any/Air; a directional `RouteSegment` cache with invalidation + an explicit Recompute and an Estimated→Measured upgrade path; off-circuit background compute; per-stop Dwell Time; an itinerary timeline obeying the **aggregate-honesty rule** (a total inherits the lowest fidelity it sums) and a soft time-budget overrun. Ships entirely on the Mock — no routing infra.
- **Epic 3 — Assisted ordering.** A "Sort in Traveling Salesman order" button (in-process NN + 2-opt over an on-demand Distance Matrix, p95 ≤ 3 s for N≤30, never worse than pre-sort); and MCP `TripTools` so a connected agent can assign order + Start/Finish + dwell over the existing authenticated `/mcp`. All four ordering paths (drag, keyboard, TSP, MCP) write the same 1-based `OrderIndex` through one `TripOrderingService`.
- **Epic 4 — Measured road routing via OSRM (optional Phase 2).** An opt-in self-hosted `OsrmTravelTimeProvider` returns **Measured** road durations + geometry for Drive/Walk/Cycle; legs with geometry render **solid road-shaped**, the rest stay dashed/muted; Estimated legs upgrade to Measured live; OSM/ODbL attribution appears when OSRM is active. OSRM is a profile-gated docker-compose sidecar — **never a launch dependency**; the default deployment runs on the Mock with zero new infra.

---

## Key decisions & rationale

Condensed from the architecture decision log (D1–D11 → AR-1…AR-12). See [architecture.md](architecture.md) for the full record.

- **Additive lens, not a new entity (D-scope).** Trip data hangs off `PoiCollection`/`PoiCollectionItem` + one new `RouteSegment` cache entity — so a plain collection is never forced to become a trip (SM-C1), and toggling never mutates membership.
- **One migration, up front (AR-1).** `AddTripPlanning` added every trip column + the cache table in Story 1.1 so no later story needed a second migration. Net **0 further migrations** across Epics 2–4.
- **Provider seam + universal fallback (AR-2/AR-3).** A per-leg `ITravelTimeProvider` with the haversine **Mock** as the shipping default and the Estimated haversine as the universal fallback; OSRM is one config-selected provider, opt-in. The cache is the single source of truth — the map widget never calls a provider directly (D2a).
- **Directional cache + single ordering write-path (AR-4/AR-11).** `RouteSegment` is keyed directionally `(From, To, Mode)` (A→B ≠ B→A); all ordering paths funnel through one `TripOrderingService` method (1-based `OrderIndex`, contiguous/unique). Canonical units fixed at the edges: **seconds**, **meters**, **minutes**.
- **Off-circuit background compute (AR-5).** Travel-time computation mirrors the enrichment service (per-worker DbContext, `SqliteWriteLock`, Polly-wrapped calls), surfacing to the UI via `StateChanged` — long work never blocks the Blazor circuit.
- **In-process TSP, no OR-Tools (AR-6).** NN + 2-opt with **full tour-cost evaluation** per trial (correct for asymmetric matrices, not just the cheap boundary-edge delta).
- **Degrade-by-throwing (Epic 4).** OSRM signals no-route/unreachable/timeout by **throwing** into the existing degradation branch — graceful degradation, observability, and the "never blank" guarantee with no second fallback path.
- **Honesty as a first-class rule (UX-DR4/5/6).** Line solidity = geometric fidelity (only Measured-with-geometry is solid); unmeasured legs show "—"; aggregates inherit the lowest fidelity they sum. The product never fakes precision.

---

## Architecture deltas (vs. before this feature)

- **New vertical slice `Services/Trip/`** (interface-first): `ITravelTimeProvider` + `MockTravelTimeProvider`/`OsrmTravelTimeProvider`, `TripOrderingService`, `DistanceMatrixService` + `TspSolver`, `RouteSegmentInvalidationService`, `TravelTimeComputationBackgroundService` + trigger/progress, `ItineraryTimeline`. DI in `Configuration/TripServicesExtensions.cs` (a deliberate parameterless overload for the integration host + an `IConfiguration` overload for production).
- **New `RouteSegment` entity** + trip fields on `PoiCollection`/`PoiCollectionItem`; `TravelMode`/`Fidelity` persisted as strings with EF check constraints (PoiCategory precedent).
- **New `travel-time` Polly pipeline** and a named `osrm` `IHttpClientFactory` client.
- **UI under `Components/Shared/Trip/`** with desktop + `Mobile*` split, driven by a sealed Transient `TripViewModel` (`StateChanged`, `IAsyncDisposable`); Leaflet leg/marker/attribution interop in `wwwroot/js/leafletInterop.js` (incl. an inlined precision-5 polyline decoder, no CDN).
- **MCP surface gained `TripTools`** on the existing three-tier `/mcp` auth — no new unauthenticated surface.
- **Optional OSRM docker-compose sidecars** behind a `profiles: ["osrm"]` gate + an operator guide (`osrm.md`).

See [trip-planning.md](trip-planning.md) for the as-built detail, including documented deviations from the plan (e.g. `MockTravelTimeProvider` naming, Scoped `TripOrderingService`, `/route`-per-leg + encoded polyline rather than `/table` + GeoJSON).

---

## Lessons (from the epic retrospectives)

- **The single ordering write-path investment paid off four times.** Drag, keyboard, TSP-Sort, and MCP all plugged into one `ArrangeWithPins → Renumber → SetOrderAsync` chain with no second writer (Epic 3 retro).
- **Standing gate A3 — run the Trip integration filter after any DI / VM-ctor / hosted-service change.** Adding a dependency to a service the integration host composes by hand was the recurring failure mode (2.1, 2.4, 3.1, 4.2); the gate caught/pre-empted it every time.
- **Pure, DB-free algorithm cores** (`TspSolver`, `ItineraryTimeline`) kept the hard logic fully unit-testable away from I/O.
- **Validate a never-invalidated cache state at write time (A10, Epic 4).** Because Measured/Manual `RouteSegment` rows are deliberately never overwritten, a *defective* such row is permanent and invisible to recompute — so OSRM refuses to emit a Measured result it can't back with geometry (throws → degrades).
- **The fresh-context adversarial review earned its keep every epic** — it caught real honesty/robustness bugs the green unit suite missed (e.g. a Placeholder leg showing a real time in 2.2; a geometry-less Measured row in 4.1).
- **Standing test conventions:** namespace `LucidCartographer.Tests` regardless of folder (A8); treat all-pairs cache computations as directional/asymmetric (A9).

---

## Known follow-ups (tracked, non-blocking)

- **A7** — promote the `OrderIndex`/pin write-path to a single gated read-validate-write transaction before any multi-writer/shared deployment (MCP is now an off-circuit writer).
- **A6-residual / `/table`** — scope recompute invalidation to a collection's ordered pairs; optional OSRM `/table` batch cache warm-up. Both optimizations only.
- **JS-render coverage** — a Playwright-DOM harness would close the standing map-rendering blind spot (`StubMapService` no-ops Leaflet): leg solid-vs-dashed render, decoded vertices, and the OSM/ODbL attribution control.
- **Air great-circle curve** — cosmetic; Air legs currently render as straight dashed connectors.

The Trip Planning capability is functionally complete; no further epic is planned.
