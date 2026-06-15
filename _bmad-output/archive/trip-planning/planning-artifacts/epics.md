---
stepsCompleted: [1, 2, 3, 4]
inputDocuments:
  - _bmad-output/planning-artifacts/prds/prd-maps_editor-2026-06-11/prd.md
  - _bmad-output/planning-artifacts/prds/prd-maps_editor-2026-06-11/addendum.md
  - _bmad-output/planning-artifacts/architecture.md
  - _bmad-output/planning-artifacts/ux-designs/ux-maps_editor-2026-06-11/DESIGN.md
  - _bmad-output/planning-artifacts/ux-designs/ux-maps_editor-2026-06-11/EXPERIENCE.md
  - _bmad-output/project-context.md
project_name: 'maps_editor'
user_name: 'Yurik'
date: '2026-06-11'
---

# maps_editor (Trip Planning for Collections) - Epic Breakdown

## Overview

This document provides the complete epic and story breakdown for the **Trip Planning** capability of LucidCartographer, decomposing the requirements from the PRD, the UX Design (DESIGN.md + EXPERIENCE.md), and the Architecture Decision Document into implementable stories. Trip Planning is an **additive lens** over an existing POI Collection — no new top-level entity.

## Requirements Inventory

### Functional Requirements

**Feature 4.1 — Trip View Toggle & Stop Ordering**
- FR-1: A user can toggle Trip View on/off for any Collection. Off shows the plain Collection (no Stop Order numbers, Legs, or timeline); on reveals Stop Order badges and the trip panel. Toggle state is restored on reopen; toggling never modifies/reorders/deletes POI membership.
- FR-2: The system assigns and persists a Stop Order (1..N) for the Stops of a Trip — contiguous, gap-free, unique. A never-ordered Collection receives a deterministic seed order (POI added-date ascending) on first Trip-View open. Adding a POI appends as last Stop; removing re-compacts.
- FR-3: A user can drag a Stop to a new position; Stop Order, Legs, Travel Times, and the timeline update immediately and persist. A manual reorder overrides any prior TSP-Sort. Pinned Start (Order 1) and Finish (Order N) keep their slots — drag reorders interior Stops only.
- FR-4: The system identifies Stops without usable coordinates, labels them **Unplaceable**, excludes them from the map/Legs/Distance Matrix, but keeps them in the Trip without breaking Stop Order numbering.
- FR-17: The Trip View toggle is a visible control in the Collection view's **filtered-results region** (not a menu), present/enabled on Collections with ≥2 placeable POIs, on **both** desktop and mobile render paths.

**Feature 4.2 — Route Visualization on the Map**
- FR-5: The map draws a line for each Leg between consecutive Stops in Stop Order, including the closing Leg of a Roundtrip (N Legs roundtrip; N−1 open path). Reorder/TSP-Sort redraws Legs without full page reload. Markers display their Stop Order number.
- FR-6: For Drive/Walk/Cycle Legs, when the active provider returns road geometry (Measured), the map draws road-shaped lines; otherwise a straight connector. Non-Measured connectors are visually distinguishable (line style) consistent with the Leg's Fidelity.
- FR-7: Selecting a Stop in the list highlights/pans to it on the map and vice versa; clicking a marker scrolls its list row into view. Reuses existing marker-click interop without regressing popups/tooltips.

**Feature 4.3 — Travel Time & Distance Computation**
- FR-8: A user can set the Travel Mode for a Trip to Any/Air, Drive, Walk, or Cycle. Changing mode invalidates prior-mode cached times and triggers recompute. Any/Air does not call a Measured provider; absent a Manual entry it carries **Placeholder** Fidelity. A user can enter a **Manual** Travel Time per Any/Air Leg (e.g. a flight duration) that overrides the placeholder.
- FR-9: The system obtains Travel Time + distance for each Leg from the configured Travel-Time Provider under the Trip's Travel Mode, each value carrying its Fidelity. Drive/Walk/Cycle with a Measured provider → **Measured**. Each Leg shows time, distance, Fidelity badge; the Trip total = Σ Leg Travel Times.
- FR-10: When the active provider cannot serve a Leg (unreachable, or reachable but no route/out-of-coverage) or the mode is Any/Air, the system falls back to the Estimated (haversine) provider rather than failing — no Leg blank, no error. A later recompute can upgrade Estimated → Measured.
- FR-11: Per-pair Travel-Time results are cached by `(FromStop, ToStop, Travel Mode, Provider)` and reused until an input changes; both displayed Legs and the on-demand Distance Matrix read this cache. A no-op reorder (all consecutive pairs already cached) triggers NO recompute. Invalidation on coords/mode/provider/assumed-speed change. Estimated→Measured upgrade is explicit (recompute action + provider-available signal), not silent. Computation runs off the request thread with a pending/computing state.

**Feature 4.4 — Dwell Time & Itinerary Timeline**
- FR-12: A user can set a Dwell Time on any Stop, stored per Stop on the Collection–POI membership (same POI carries different dwell across Trips). No dwell ⇒ zero contribution. An overnight is just a large Dwell Time.
- FR-13: The system computes arrival time per Stop and the Trip's finish time from Stop Order, Travel Times, and Dwell Times. Start's dwell counts once at the beginning; `arrival(k+1) = arrival(k) + Dwell(k) + TravelTime(k→k+1)`; a Roundtrip adds a distinct return arrival via the closing Leg. With a Trip start time → clock arrivals; without → relative offsets. Unplaceable Stops contribute Dwell but no Travel Time. Placeholder-fidelity Legs propagate uncertainty downstream. An optional time-budget overrun is flagged softly.

**Feature 4.5 — Start / Finish & Roundtrip**
- FR-14: A user can set any Stop as Start (pinned to Order 1) and optionally any other Stop as Finish (pinned to Order N). Finish unset ⇒ Roundtrip (closing Leg returns to Start); distinct Finish ⇒ open path. No Stop ever holds two Stop Order values.

**Feature 4.6 — Ordering: Manual, TSP-Sort, and MCP**
- FR-15: A user can trigger an explicit **"Sort in Traveling Salesman order"** action (NN + 2-opt over the Distance Matrix) that reorders Stops to minimize total Travel Time, keeping Start/Finish pinned. On-demand only — never automatic. Result total ≤ pre-sort; completes interactively for N≤30; overridable by a subsequent manual drag.
- FR-16: An external agent can read ordered Stops + computed Legs and **assign Stop Order numbers** (and set Start/Finish, Dwell Time) for a Collection's POIs through the MCP server, honoring the existing `/mcp` auth guard. An MCP-assigned order persists identically to a manual drag and remains drag-editable.

### NonFunctional Requirements

- NFR1 (Performance): Distance Matrix + TSP-Sort for N≤30 complete within p95 ≤ 3 s with a warm matrix (SM-5). Map redraw on reorder is incremental, not full-page.
- NFR2 (Reliability / graceful degradation): A provider outage or out-of-coverage result must never break Trip View — the Estimated (haversine) fallback keeps every Leg populated and Fidelity-badged (FR-10).
- NFR3 (UI responsiveness / layering): ViewModel-driven state per Component → ViewModel → Service → Data layering; long compute runs off the circuit thread via a background job + `StateChanged` notification (mirrors `PoiEnrichmentBackgroundService`).
- NFR4 (Accessibility): Order badges, Legs, and timeline values carry `aria-label`s; computing states use `aria-live`; stop reordering has a **keyboard-accessible path** (not drag-only). Implemented on **both** desktop and `Mobile*Screen` paths. Mobile touch targets ≥ ~44px; safe-area insets honored.
- NFR5 (Internationalization): All new UI text goes through `UiStrings` — no hardcoded strings.
- NFR6 (Observability): Travel-time computations and provider failures are logged, distinguishing Measured vs Estimated/Placeholder/Manual Legs (feeds SM-3).
- NFR7 (Privacy / data residency): With a self-hosted provider (Mock or OSRM), Stop coordinates stay within the deployment. Any out-calling provider must surface its data egress to the operator **before the first out-call** (firm consent guard).
- NFR8 (Licensing): An OSM-based provider's data carries ODbL → the UI must show OSM attribution on the map on both surfaces when such a provider is active.
- NFR9 (Cost): Default (Mock) and self-hosted OSRM incur no per-request cost; a hosted BYO-key provider is the only metered option and is opt-in, never default.
- NFR10 (Dual-surface honesty / counter-metrics): Trip View is optional and additive — plain Collection usage must not be forced to become a Trip (SM-C1); recomputation must stay rare via the cache (SM-C2).

### Additional Requirements

_Technical/implementation requirements from the Architecture Decision Document (D1–D11) and the addendum that shape epic & story sequencing._

- **AR-1 (D1 schema migration — first story):** A single EF Core migration `AddTripPlanning` via startup `MigrateAsync` adds: `PoiCollectionItem.OrderIndex` (int, 1-based), `PoiCollectionItem.DwellMinutes` (int?); `PoiCollection.TravelMode`, `StartPoiId`, `FinishPoiId`, `TripStartTime`, `TimeBudgetMinutes`, `TripViewEnabled`; new `RouteSegment` entity keyed `(FromPoiId, ToPoiId, TravelMode)` with `DurationSeconds`, `DistanceMeters`, `GeometryPolyline?`, `Fidelity`, `Source`, `ComputedAt`, `Version` concurrency token. Never EnsureCreated / never hand-edit applied migrations.
- **AR-2 (D2 provider contract + Mock):** `ITravelTimeProvider.GetLegAsync(fromStop, toStop, travelMode) -> (duration, distance, Fidelity, geometry?)`; haversine **Mock** is the shipping default (Estimated, zero infra). Config-selected per deployment; one active provider with Estimated as universal fallback.
- **AR-3 (D2/D2a OSRM — optional Phase 2):** `OsrmTravelTimeProvider` queries OSRM `/table` + `/route` (`geometries=geojson`) → Measured + geometry. Optional docker-compose sidecar (region-scoped extract, per-profile container), NOT a launch dependency.
- **AR-4 (D3/D11 cache + matrix):** `RouteSegment` cache + centralized invalidation (coords/mode/provider/assumed-speed); on-demand N×N Distance Matrix reads/writes the same cache; explicit Estimated→Measured upgrade.
- **AR-5 (D4 background compute):** `TravelTimeComputationBackgroundService` + `TravelTimeTrigger` mirroring enrichment — per-worker DbContext, `SqliteWriteLock`, Polly-wrapped provider calls; resolves to UI via `StateChanged`.
- **AR-6 (D5 TSP-Sort):** In-process C# NN + 2-opt (~150 lines, no OR-Tools) in `TripOrderingService`; pins Start/Finish, swaps interior edges only; result ≤ pre-sort total.
- **AR-7 (D6 map rendering):** Leaflet Routing Machine (version-pinned) with a **custom LRM `IRouter`** returning our cached Legs — LRM never calls OSRM directly; the server-side cache stays single source of truth. Phase 1 straight connectors; Phase 2 road geometry. Air/non-Measured = dashed great-circle/muted; only Measured = solid.
- **AR-8 (D7 MCP):** New `TripTools` in `Services/Mcp/` (`GetTripStops`, `AssignStopOrder`, `SetStartFinish`, `SetDwellTime`) on the existing three-tier `/mcp` auth — no new unauthenticated surface.
- **AR-9 (D8 keyboard reorder — a11y build-blocker):** Keyboard-focusable move-up/move-down controls per Stop row, `aria-label`led, announced via `aria-live`; identical on desktop and `Mobile*Screen`. Drag remains the pointer path.
- **AR-10 (D9 Any/Air speed):** A single configurable assumed speed for all Any/Air Legs, surfaced as a badged Placeholder/Estimated; Manual per-Leg entry overrides.
- **AR-11 (Pattern enforcement):** Canonical units — durations in **seconds**, distances in **meters**, dwell/budget in **minutes**; convert at UI edge only. `OrderIndex` **1-based**. Cache key is **directional** (A→B ≠ B→A). `TravelMode`/`Fidelity` persisted as **strings** with EF check constraints (PoiCategory precedent). All four ordering paths (drag, keyboard, TSP, MCP) write the same `OrderIndex` through one `TripOrderingService` method. Tag new decisions with `TRIP-*` comment codes. New code introduces no group-B analyzer violation; no `ConfigureAwait(false)`.
- **AR-12 (Structure):** New vertical slice `Services/Trip/` (interface-first); `TripViewModel` (sealed, Transient, `StateChanged`, `IAsyncDisposable`); DI in new `Configuration/TripServicesExtensions.cs`; Trip UI under `Components/Shared/Trip/` with desktop + `MobileTrip*` split.

### UX Design Requirements

_First-class actionable items extracted from DESIGN.md (visual system) and EXPERIENCE.md (behavior, states, flows)._

- **UX-DR1 (Trip View toggle):** Switch in the collection's filtered-results region. Off = plain collection; On = `primary`-accented active state. Visible/enabled only at ≥2 placeable POIs; exposes `aria-pressed` and announces on/off state. State persists per-collection.
- **UX-DR2 (Stop-order badge):** Small numbered circle on each stop in the list and on the map marker — `primary` fill, `on-primary` numeral, `text-xs` weight 700. Start uses a distinct glyph/ring; Finish likewise.
- **UX-DR3 (Stop list row):** Drag handle · order badge · POI name · dwell-time field · running timeline value · keyboard move up/down. Reorderable; echoes the POI table row but trip-scoped.
- **UX-DR4 (Route leg line solidity = geometric fidelity):** Only **Measured** legs render solid, full-weight, `primary`. **Estimated, Manual, Placeholder, and Air** all render **dashed AND muted** (no real geometry). Air = dashed great-circle; closing roundtrip leg uses the same language. A Manual flight time is trusted via its badge, never a solid line.
- **UX-DR5 (Fidelity badge):** Small pill per leg time — **Measured** (`secondary`/confirmed), **Estimated** (`on-surface-muted`), **Manual** (`primary`, trusted). **Placeholder is internal-only**; any unmeasured/unentered leg shows **"—"** (em-dash) in the user-facing time slot, never a Placeholder badge. `text-xs`, never larger than the time it qualifies.
- **UX-DR6 (Itinerary timeline):** Per stop shows relative cumulative offset (always) + wall-clock arrival (only when a trip start time is set), with finish/return at the end. **Aggregate honesty rule:** a running total/arrival inherits the **lowest** fidelity among the legs it sums (e.g. `~18:10 · Estimated`), never a clean confident time over mixed fidelity. Soft time-budget overrun uses `warn` amber (not `tertiary` red); budget is an optional per-trip field.
- **UX-DR7 (Travel-mode selector):** Per-trip segmented control: Any/Air · Drive · Walk · Cycle, `primary` active segment. Changing mode re-requests leg times in background.
- **UX-DR8 (Manual time entry per leg):** User can type a leg time (e.g. flight duration) → badge becomes **Manual** (trusted), recomputes timeline; the leg's map line stays dashed+muted.
- **UX-DR9 (Recompute travel times action):** Explicit user-initiated action re-requests leg times; when real geometry returns, an Estimated leg **upgrades to Measured** — line goes solid, badge updates, timeline recomputes; upgrade lands via `StateChanged`, never silently on a stale screen.
- **UX-DR10 (State treatments):** Defined treatments for: Trip View unavailable (toggle absent/disabled, never an error); Unplaceable POI ("Not placeable", excluded from routing, kept in collection); Leg computing (pending via `aria-live`, incremental redraw); Routing provider down (dashed+muted, Estimated badges, approximate copy); Mixed-fidelity total (qualified to lowest); Unplaceable stop in timeline (dwell accrues, no travel time); Time-budget overrun (soft `warn`).
- **UX-DR11 (Voice & tone microcopy):** Honest, factual, complete sentences via `UiStrings` — provenance on every number ("Estimated"/"Measured"/"Manual"), "Not placeable — no coordinates…", "Couldn't reach the routing engine — showing straight-line estimates." No hype, exclamation marks, or false precision.
- **UX-DR12 (Dual-surface render):** Trip View toggle, stop list, timeline, ordering actions, and all accessibility affordances exist on **both** desktop (panel beside map) and mobile (`Mobile*Screen`: map ~46% top over bottom panel/sheet) — mobile is the on-the-road scenario, not a degraded view. Dark mode first-class. OSM/ODbL attribution visible on both surfaces when an OSM-based provider is active.
- **UX-DR13 (List ↔ map two-way sync):** Selecting a stop in the list pans/highlights its marker; clicking a marker scrolls its list row into view — a core trip interaction.
- **UX-DR14 (Start/Finish controls):** Designate a stop as Start (pinned order 1) and optionally Finish (pinned order N); Roundtrip (closed loop back to Start) is the default.

### FR Coverage Map

- FR-1: Epic 1 — Toggle Trip View on/off, restore state
- FR-2: Epic 1 — Seed + persist contiguous 1..N Stop Order
- FR-3: Epic 1 — Drag + keyboard reorder
- FR-4: Epic 1 — Flag Unplaceable, exclude from routing, keep in collection
- FR-5: Epic 1 — Draw ordered straight Legs + roundtrip close
- FR-7: Epic 1 — List↔map two-way selection sync
- FR-14: Epic 1 — Designate Start (order 1) / Finish (order N) / roundtrip
- FR-17: Epic 1 — Discoverable toggle in filtered-results region (≥2 placeable)
- FR-8: Epic 2 — Travel Mode selector + Any/Air Manual time
- FR-9: Epic 2 — Per-Leg Travel Time + distance + Fidelity badge from provider
- FR-10: Epic 2 — Graceful degradation to haversine, no blank legs
- FR-11: Epic 2 — Directional cache, invalidation, Estimated→Measured upgrade, recompute
- FR-12: Epic 2 — Dwell Time per Stop on membership
- FR-13: Epic 2 — Itinerary Timeline walk + aggregate-honesty + budget overrun
- FR-15: Epic 3 — TSP-Sort button (NN+2-opt), pinned endpoints
- FR-16: Epic 3 — MCP TripTools ordering on authenticated /mcp
- FR-6: Epic 4 — Road-shaped geometry when a Measured provider (OSRM) supplies it

_All 17 FRs mapped, each to exactly one epic._

## Epic List

### Epic 1: Trip View — turn a collection into an ordered, mapped loop
Flip Trip View on and the collection becomes a Trip: numbered stop badges, connecting legs drawn across the map, drag-or-keyboard reorder, Start/Finish designation with roundtrip default, unplaceable POIs flagged (not dropped), and the whole arrangement persisted per-collection. The spatial backbone of UJ-1. First story is the AR-1 EF Core migration; legs are straight connectors (Phase 1); keyboard-reorder a11y build-blocker (AR-9) is included; both desktop and `Mobile*Screen` paths.
**FRs covered:** FR-1, FR-2, FR-3, FR-4, FR-5, FR-7, FR-14, FR-17

### Epic 2: Travel times, dwell & the honest itinerary timeline
The loop tells time: pick a Travel Mode; each leg shows a travel time with an honest Fidelity badge (em-dash for unmeasured); set Dwell Time per stop; read a running itinerary timeline obeying the aggregate-honesty rule and flagging time-budget overrun; enter a Manual time for an Any/Air leg. Realizes UJ-1's climax and UJ-3. Introduces the provider contract + haversine Mock (AR-2), the per-pair cache + invalidation + Estimated→Measured upgrade + Recompute (AR-4), and the background compute service (AR-5). Ships entirely on the Mock — no routing infra.
**FRs covered:** FR-8, FR-9, FR-10, FR-11, FR-12, FR-13

### Epic 3: Assisted ordering — TSP-Sort & MCP agent
Stop ordering by hand: one button reorders a messy collection into an efficient loop (NN + 2-opt, p95 ≤ 3 s for N≤30); or a connected AI agent over MCP assigns the order (and Start/Finish/dwell). Either result stays freely drag-editable. Realizes UJ-2. Adds the on-demand Distance Matrix over the shared cache; all ordering paths write the same `OrderIndex` through `TripOrderingService`.
**FRs covered:** FR-15, FR-16

### Epic 4: Measured road routing via OSRM (Phase 2, optional)
Real road-shaped lines and Measured travel times: enabling OSRM redraws Drive/Walk/Cycle legs along roads (solid) and upgrades Estimated legs to Measured; OSM attribution appears. Split off as a deliberate risk/infra boundary — OSRM is an optional docker-compose sidecar, NOT a launch dependency (Architecture D2a). Realizes the Measured side of FR-9/10/11 in practice; carries ODbL attribution (NFR8) and, if an out-calling provider is ever added, the egress guard (NFR7).
**FRs covered:** FR-6

---

## Epic 1: Trip View — turn a collection into an ordered, mapped loop

Flip Trip View on and an existing POI Collection becomes a Trip: numbered stop badges, connecting legs drawn across the map, drag-or-keyboard reorder, Start/Finish designation with roundtrip default, unplaceable POIs flagged (not dropped), and the whole arrangement persisted per-collection. Delivers the spatial backbone of UJ-1. Legs are straight connectors (Phase 1 — no travel times yet). Every affordance lands on both desktop and `Mobile*Screen` paths, with the keyboard-reorder a11y path included.

### Story 1.1: Trip schema foundation (EF Core migration)

As a LucidCartographer maintainer,
I want the database schema extended with trip fields and a route-segment cache table,
So that every later Trip View story has the persistent shape it needs without a second migration.

**Acceptance Criteria:**

**Given** the existing `PoiCollection` and `PoiCollectionItem` entities
**When** the `AddTripPlanning` migration is applied via startup `MigrateAsync`
**Then** `PoiCollectionItem` gains `OrderIndex` (int, 1-based) and `DwellMinutes` (int?, nullable)
**And** `PoiCollection` gains `TravelMode`, `StartPoiId` (nullable FK), `FinishPoiId` (nullable FK), `TripStartTime` (nullable), `TimeBudgetMinutes` (int?, nullable), and `TripViewEnabled` (bool)
**And** a new `RouteSegment` entity exists keyed `(FromPoiId, ToPoiId, TravelMode)` with `DurationSeconds`, `DistanceMeters`, `GeometryPolyline` (nullable), `Fidelity`, `Source`, `ComputedAt`, and a `Version` concurrency token.

**Given** the new `TravelMode` and `Fidelity` enums
**When** the entities are configured in `AppDbContext`
**Then** both enums are persisted as strings with an EF check constraint (matching the `PoiCategory` precedent), not int-backed
**And** indexes exist on the `RouteSegment` cache key and the new FK columns.

**Given** the existing migration discipline
**When** the migration is authored
**Then** it is a new migration applied through startup `MigrateAsync` (never `EnsureCreated`, never hand-edited after apply)
**And** the build passes with warnings-as-errors and no group-B analyzer violations
**And** new design decisions carry searchable `TRIP-*` comment codes.

### Story 1.2: Toggle Trip View and seed Stop Order

As a self-hoster viewing a collection,
I want a discoverable Trip View toggle that reveals an ordered Trip,
So that I can switch a plain collection into a trip and back without losing anything.

**Acceptance Criteria:** _(FR-1, FR-2, FR-17, UX-DR1, UX-DR2)_

**Given** a collection with ≥2 placeable POIs in the map filtered-results region
**When** the page renders on either desktop or mobile
**Then** a Trip View toggle is visible in the filtered-results region (not in a menu), exposing `aria-pressed` and announcing its on/off state
**And** on a collection with fewer than 2 placeable POIs the toggle is hidden or disabled (never an error).

**Given** a collection that has never had a Stop Order
**When** Trip View is toggled on for the first time
**Then** a deterministic seed Stop Order (POI added-date ascending) is assigned, contiguous and gap-free (1..N), and persisted
**And** each stop shows a `primary`-filled order badge in the stop list and on its map marker.

**Given** Trip View is on
**When** I toggle it off
**Then** all trip affordances disappear and the plain collection (same POI set and controls) is restored with no membership change
**And** when I reopen the collection later, the toggle state and Stop Order are restored (persisted per-collection).

**Given** the collection membership changes while a Stop Order exists
**When** a POI is added or removed
**Then** an added POI is appended as the new last Stop and a removed POI's slot is re-compacted so the order stays contiguous.

### Story 1.3: Render ordered stops, connecting legs and the stop panel

As a trip planner,
I want my stops drawn in order on the map with connecting legs and a side stop list,
So that I can see the shape of the loop at a glance.

**Acceptance Criteria:** _(FR-5, UX-DR3, UX-DR12, UX-DR14)_

**Given** Trip View is on for a roundtrip with N placeable stops
**When** the map renders
**Then** a straight connecting leg is drawn between each consecutive pair in Stop Order, including the closing leg from Stop N back to the Start (N legs total)
**And** a Start≠Finish open path draws N−1 legs with no closing leg
**And** every marker displays its Stop Order number.

**Given** the stop list panel
**When** Trip View is on
**Then** desktop shows the stop list beside the map and mobile shows it in the bottom panel/sheet (both render paths implemented)
**And** each row shows the order badge, POI name, a dwell-time field placeholder, and a timeline-value placeholder.

**Given** non-Measured legs (all legs in Phase 1)
**When** they are drawn
**Then** they render dashed and muted per the line-solidity = geometric-fidelity rule (only Measured legs are solid)
**And** the redraw on any order change is incremental, not a full page reload.

### Story 1.4: Two-way list ↔ map selection sync

As a trip planner,
I want selecting a stop in the list to highlight it on the map and vice versa,
So that I can connect a row to its place without hunting.

**Acceptance Criteria:** _(FR-7, UX-DR13)_

**Given** Trip View is on
**When** I select a stop row in the list
**Then** the map pans so that stop's marker is within the viewport and visually emphasized (distinct from unselected markers)
**And** the selection clears when another stop is chosen.

**Given** Trip View is on
**When** I click a stop marker on the map
**Then** its list row scrolls into view and is emphasized
**And** existing marker popup/tooltip behavior is not regressed (the sync reuses the existing marker-click interop).

### Story 1.5: Reorder stops by drag and by keyboard

As a trip planner,
I want to reorder stops by dragging or by keyboard controls,
So that I can arrange the loop the way I want, accessibly.

**Acceptance Criteria:** _(FR-3, NFR4/AR-9, UX-DR3)_

**Given** Trip View is on
**When** I drag a stop to a new position and drop it
**Then** the affected range is renumbered, the order persists, and the legs and dependent views update immediately without a full reload.

**Given** keyboard-only operation
**When** I focus a stop row and activate its move-up / move-down control
**Then** the stop moves one position, the change is announced via `aria-live`, and the controls carry descriptive `aria-label`s
**And** the keyboard path is implemented identically on desktop and `Mobile*Screen`.

**Given** a pinned Start (Order 1) or Finish (Order N)
**When** I reorder
**Then** only interior stops move; dropping a stop at the first/last position does not transfer the Start/Finish role to it (role changes only via Story 1.7)
**And** a manual reorder persists and would override any prior assisted ordering.

### Story 1.6: Flag unplaceable stops

As a trip planner with an incompletely-enriched collection,
I want POIs without coordinates kept in the trip but clearly excluded from the route,
So that nothing is silently dropped and my loop stays honest.

**Acceptance Criteria:** _(FR-4, UX-DR10, UX-DR11)_

**Given** a stop whose POI has a null latitude or longitude
**When** Trip View is on
**Then** the stop is labelled "Not placeable" in the stop list (copy via `UiStrings`), kept in the collection, and not drawn on the map or included in any leg
**And** it is excluded from any all-pairs routing computation.

**Given** unplaceable stops exist among placeable ones
**When** the order is displayed
**Then** the Stop Order numbering of the remaining placeable stops is not broken by the unplaceable ones.

### Story 1.7: Designate Start, Finish and roundtrip

As a trip planner,
I want to set where my loop starts and (optionally) ends,
So that the trip is an honest roundtrip or a deliberate open path.

**Acceptance Criteria:** _(FR-14, UX-DR2, UX-DR14)_

**Given** Trip View is on
**When** I designate a stop as Start
**Then** it is pinned to Stop Order 1, shown with a distinct Start glyph/ring, and the map loop and stop list anchor on it.

**Given** a Start is set and Finish is left unset
**When** the loop renders
**Then** the Trip is a Roundtrip — the closing leg returns from Order N to the Start — and this is the default shape.

**Given** I set a distinct stop as Finish
**When** the loop renders
**Then** that stop is pinned to Stop Order N with a distinct Finish glyph, the Trip becomes an open path ending there (no closing leg), and no stop ever holds two Stop Order values.

---

## Epic 2: Travel times, dwell & the honest itinerary timeline

The loop starts telling time. A Travel Mode selector drives per-leg travel times supplied by a pluggable Travel-Time Provider (haversine **Mock** shipping default), each value carrying — and badging — its Fidelity, with an em-dash for anything unmeasured. Dwell Time per stop feeds an Itinerary Timeline that obeys the aggregate-honesty rule and flags time-budget overrun. Realizes UJ-1's climax ("does the day fit?") and UJ-3. Ships entirely on the Mock provider with no routing infrastructure; durations are stored in seconds, distances in meters, dwell/budget in minutes (converted only at the UI edge).

### Story 2.1: Per-leg travel time from the provider, Fidelity-badged

As a trip planner,
I want each leg to show a travel time and distance with an honest provenance badge,
So that I can read how long each hop takes and how much to trust it.

**Acceptance Criteria:** _(FR-9, AR-2, AR-5, UX-DR5)_

**Given** the Travel-Time Provider contract `GetLegAsync(fromStop, toStop, travelMode) → (duration, distance, Fidelity, geometry?)` with the haversine **Mock** as the configured default
**When** a trip's legs are computed
**Then** each leg obtains a duration (seconds) and distance (meters) from the active provider, every value carrying a `Fidelity` of Measured / Estimated / Placeholder / Manual
**And** the Mock provider yields **Estimated** fidelity (straight-line × assumed speed).

**Given** computed legs
**When** the stop list and/or map render them
**Then** each leg shows its travel time, distance, and a Fidelity badge (Measured = `secondary`, Estimated = `on-surface-muted`, Manual = `primary`)
**And** an unmeasured/unentered leg shows its time as an em-dash "—", never a Placeholder badge in the user-facing slot
**And** the trip's total travel time equals the sum of its legs' travel times.

**Given** computation may be non-trivial
**When** legs are (re)computed
**Then** it runs off the circuit thread in a `TravelTimeComputationBackgroundService` (mirroring `PoiEnrichmentBackgroundService`: per-worker DbContext, `SqliteWriteLock`, Polly-wrapped calls), the UI shows a pending/computing state via `aria-live`, and results land via the ViewModel's `StateChanged` without a manual refresh
**And** computed results are written to the `RouteSegment` cache.

### Story 2.2: Select Travel Mode and enter a manual Any/Air time

As a trip planner,
I want to choose how I'm travelling and type a known time for a flight leg,
So that the loop's times reflect my mode and my real knowledge.

**Acceptance Criteria:** _(FR-8, AR-10, UX-DR7, UX-DR8)_

**Given** Trip View is on
**When** I pick a Travel Mode from the segmented selector (Any/Air · Drive · Walk · Cycle, `primary` active segment)
**Then** the choice is per-trip, cached times computed under the prior mode are invalidated, and recomputation is triggered.

**Given** Any/Air mode with a single configurable assumed speed
**When** a leg has no manual time
**Then** its time carries **Placeholder** fidelity and is shown as "—" (never presented as a real door-to-door time).

**Given** an Any/Air leg
**When** I enter a manual travel time (e.g. a flight duration)
**Then** that leg's value carries **Manual** fidelity, overrides the placeholder, and recomputes the timeline
**And** the leg's map line stays dashed+muted (trust carried by the badge, not the line).

### Story 2.3: Graceful degradation to straight-line estimates

As a trip planner whose routing engine is unavailable,
I want legs to fall back to estimates instead of erroring,
So that my loop still works and still tells me the times are approximate.

**Acceptance Criteria:** _(FR-10, NFR2, UX-DR10, UX-DR11)_

**Given** the active provider is unreachable, or reachable but returns no route / out-of-coverage, or the mode is Any/Air
**When** legs are computed
**Then** every affected leg falls back to the Estimated (haversine) provider, is badged **Estimated**, and is never left blank — the feature does not error out
**And** the affected legs render dashed+muted and copy notes the times are approximate (via `UiStrings`).

**Given** a provider failure during compute
**When** the result is surfaced
**Then** the failure is logged distinguishing Measured vs Estimated/Placeholder/Manual legs (observability), and the loop still orders.

### Story 2.4: Cache invalidation, recompute & Estimated→Measured upgrade

As a self-hoster,
I want computed times cached and only recomputed when something really changed,
So that the app stays responsive and never hammers the provider.

**Acceptance Criteria:** _(FR-11, AR-4, NFR1, NFR10/SM-C2, UX-DR9)_

**Given** the directional cache keyed `(FromPoiId, ToPoiId, TravelMode, Provider)` (A→B and B→A are distinct rows)
**When** a Stop Order change introduces no new `(From, To, Mode)` pair (all consecutive pairs already cached)
**Then** no recomputation is triggered — only the displayed legs change.

**Given** a cached entry
**When** either endpoint's coordinates, the Travel Mode, the active Provider, or the Any/Air assumed-speed setting changes
**Then** that entry is invalidated and recomputed on the next trigger.

**Given** legs currently served at Estimated fidelity
**When** I invoke an explicit "Recompute travel times" action and/or a provider-available signal fires
**Then** eligible entries are recomputed and may **upgrade** Estimated → Measured; the upgrade is never silent — it lands via `StateChanged` (line goes solid, badge updates, timeline recomputes).

### Story 2.5: Set Dwell Time per stop

As a trip planner,
I want to set how long I'll linger at each stop,
So that my timeline reflects time spent, not just time travelling.

**Acceptance Criteria:** _(FR-12, UX-DR3)_

**Given** a stop in Trip View
**When** I set a Dwell Time on it
**Then** the value is stored in minutes on the Collection–POI membership (`DwellMinutes`), so the same POI can carry different dwell across trips.

**Given** dwell values
**When** the timeline computes
**Then** a stop with no Dwell Time set contributes zero
**And** an overnight is expressible purely as a large Dwell Time (e.g. 600 minutes) with no special "day" handling.

### Story 2.6: Compute the honest Itinerary Timeline

As a trip planner,
I want a running timeline that tells me when I arrive where and never fakes precision,
So that I can judge whether the day fits and trust the numbers.

**Acceptance Criteria:** _(FR-13, NFR10, UX-DR6, UX-DR10, UX-DR11)_

**Given** placeable stops in Stop Order with travel and dwell times
**When** the timeline computes
**Then** it walks the stops as `arrival(1) = TripStart` (or offset 0), `departure(k) = arrival(k) + Dwell(k)`, `arrival(k+1) = departure(k) + TravelTime(k→k+1)`; the Start's dwell is counted once at the beginning; and a Roundtrip produces a distinct return-to-Start arrival via the closing leg's travel time.

**Given** the timeline display
**When** it renders per stop
**Then** it always shows a relative cumulative offset (e.g. `+2h15m`) and, only when a trip start time is set, a wall-clock arrival (e.g. `14:10`), with a finish/return readout at the end.

**Given** a running total or final arrival that sums legs of differing fidelity
**When** it is shown
**Then** it inherits the **lowest** fidelity among the legs it sums (e.g. `~18:10 · Estimated`), never a clean confident time over mixed fidelity.

**Given** edge cases
**When** the timeline computes
**Then** an unplaceable stop contributes its Dwell to the running total but adds no travel time; a Placeholder-fidelity leg propagates its uncertainty to downstream arrivals; and if an optional per-trip time budget is set and exceeded, a soft `warn` (amber, not red) overrun flag is shown — with no flag when no budget is set.

---

## Epic 3: Assisted ordering — TSP-Sort & MCP agent

Stop ordering by hand. An explicit "Sort in Traveling Salesman order" button reorders a messy collection into an efficient loop (nearest-neighbor + 2-opt over an on-demand Distance Matrix, p95 ≤ 3 s for N≤30); and the existing authenticated MCP server gains trip tools so a connected AI agent can assign the order — and Start/Finish/dwell — honoring soft constraints the cost matrix can't express. Both paths are on-demand (never automatic), write the same 1-based `OrderIndex` through `TripOrderingService`, and leave the result freely drag-editable. Realizes UJ-2.

### Story 3.1: "Sort in Traveling Salesman order" button

As a trip planner with a zig-zag collection,
I want one button that reorders my stops into an efficient loop,
So that I don't have to untangle the order by hand.

**Acceptance Criteria:** _(FR-15, AR-6, NFR1, UX-DR10)_

**Given** Trip View is on with placeable stops
**When** I press "Sort in Traveling Salesman order"
**Then** an on-demand N×N Distance Matrix is built over the placeable stops from the shared `RouteSegment` cache (reusing cached pairs), and a nearest-neighbor + 2-opt search rewrites `OrderIndex`
**And** the system never reorders stops without this explicit press.

**Given** a designated Start and/or Finish
**When** the sort runs
**Then** it keeps the Start at Order 1 and the Finish at Order N (swapping interior edges only); with no Start/Finish designated it optimizes without that pin; for a roundtrip it closes the loop.

**Given** a completed sort
**When** the result is applied
**Then** the new order's total travel time is ≤ the pre-sort order for the same stops and mode (never worse), the map and timeline redraw, and the result remains overridable by a subsequent manual drag.

**Given** a trip of up to 30 stops with a warm matrix
**When** I trigger the sort
**Then** it completes interactively (p95 ≤ 3 s); larger N still completes without the interactivity guarantee.

### Story 3.2: Assign Stop Order (and Start/Finish/dwell) via MCP

As a user with a connected AI agent,
I want the agent to read my trip and assign an order honoring soft constraints,
So that I can say "museums in the morning, rooftop bar last" and have it applied.

**Acceptance Criteria:** _(FR-16, AR-8)_

**Given** the existing authenticated `/mcp` server
**When** the trip tools are added in `Services/Mcp/TripTools.cs`
**Then** they expose, at minimum: read ordered stops + computed legs for a collection; **assign Stop Order numbers** to the collection's POIs; set Start/Finish; and set Dwell Time
**And** they ride the existing three-tier `/mcp` auth guard (LAN → API key → OAuth) with no new unauthenticated surface added.

**Given** an order assigned via MCP
**When** it is written
**Then** it goes through the same `TripOrderingService` (1-based `OrderIndex`, `SqliteWriteLock`) as a manual drag, persists identically, and is reflected in the map, times, and timeline
**And** it remains editable by a subsequent manual drag (no system reshuffle undoes the user's later edit).

---

## Epic 4: Measured road routing via OSRM (Phase 2, optional)

Real road-shaped lines and Measured travel times. Enabling an optional OSRM provider redraws Drive/Walk/Cycle legs along actual roads (solid, full-weight) and lets Estimated legs upgrade to Measured, with OSM attribution shown. This epic is a deliberate risk/infra boundary — OSRM is an optional docker-compose sidecar, **not a launch dependency** (Architecture D2a); everything in Epics 1–3 ships and works on the Mock without it. The server-side cache remains the single source of truth: the map widget never calls OSRM directly.

### Story 4.1: OSRM Measured travel-time provider

As a self-hoster who wants real road times,
I want an optional OSRM provider I can enable per deployment,
So that Drive/Walk/Cycle legs return measured durations and road geometry.

**Acceptance Criteria:** _(FR-6 enabling, AR-3, NFR7, NFR9)_

**Given** the `ITravelTimeProvider` contract
**When** `OsrmTravelTimeProvider` is implemented and config-selected
**Then** it queries OSRM `/table` (matrix) and `/route` (`geometries=geojson`) and returns **Measured** fidelity values with route geometry, mapping Walk/Cycle/Drive to the foot/bike/car profiles.

**Given** the optional sidecar
**When** an operator opts into OSRM
**Then** it runs as a docker-compose sidecar (`ghcr.io/project-osrm/osrm-backend`, version-pinned) with a region-scoped OSM extract under an opt-in compose profile, started only on opt-in (the default Mock deployment needs none of it).

**Given** a leg whose endpoint is outside the loaded coverage
**When** OSRM returns no route / out-of-coverage
**Then** that leg degrades to Estimated fidelity rather than erroring (graceful degradation preserved)
**And** because OSRM is self-hosted, coordinates stay within the deployment (no egress); the egress-consent guard applies only if a future out-calling provider is added.

### Story 4.2: Draw road-shaped legs and OSM attribution

As a trip planner with OSRM enabled,
I want my Drive/Walk/Cycle legs drawn along real roads,
So that the map shows the true shape of the route, honestly badged.

**Acceptance Criteria:** _(FR-6, AR-7, NFR8, UX-DR4, UX-DR9, UX-DR12)_

**Given** a leg for which the active provider returned road geometry (Measured)
**When** the map renders it
**Then** the line follows the roads and renders **solid, full-weight, `primary`** (the only solid state); when no geometry is available it draws a straight connector, dashed+muted
**And** Any/Air legs always remain dashed great-circle lines.

**Given** an Estimated leg after OSRM becomes available
**When** a recompute / provider-available signal fires
**Then** the leg upgrades to Measured — its line goes solid and its badge updates — landing via `StateChanged`, never silently on a stale screen.

**Given** an OSM-based provider is active
**When** the map renders on either surface
**Then** OSM/ODbL attribution is visible on both desktop and `Mobile*Screen` paths
**And** geometry is stored/encoded one consistent way project-wide (`GeometryPolyline`; null = no road geometry → dashed/muted render).
