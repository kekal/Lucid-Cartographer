---
stepsCompleted: [1, 2, 3, 4]
inputDocuments:
  - _bmad-output/planning-artifacts/prds/prd-maps_editor-2026-06-15/prd.md
  - _bmad-output/planning-artifacts/architecture.md
  - _bmad-output/planning-artifacts/ux-designs/ux-maps_editor-2026-06-15/DESIGN.md
  - _bmad-output/planning-artifacts/ux-designs/ux-maps_editor-2026-06-15/EXPERIENCE.md
  - _bmad-output/project-context.md
---

# maps_editor - Epic Breakdown

## Overview

This document provides the complete epic and story breakdown for **maps_editor**
(LucidCartographer — Trip View Realignment & Honest Schedule feature), decomposing
the requirements from the PRD, the UX Design/Experience deltas, and the Architecture
decisions (RD1–RD13) into implementable stories.

This is a **brownfield delta** on the already-shipped Trip Planning slice (Epics
1–4). It separates into three risk classes: **layout-only** (Features A, C),
**shared-layer correctness/legibility** (B, D, E, H), and **new capability** (F
per-leg mode, G multi-day schedule — the only structurally novel work, F carries the
one schema migration).

## Requirements Inventory

### Functional Requirements

**Feature A — Trip View switches the desktop list region into the trip (root)**

- FR-1: Toggling Trip View **on** makes the desktop filtered-results region **become** the trip stop list; the plain PoiTable is not shown at the same time. Toggling **off** restores the plain PoiTable and controls **unchanged** (no data loss).
- FR-2: The trip stop list renders in the **wide list region** (not a 256px side column) as a trip-scoped table, columns L→R: reorder gutter (drag handle + ▲▼) · Stop # badge (with Start/Finish glyph + ring) · Name (full POI name, address sub-line, enrichment icon) · Dwell (HH:MM picker, FR-30) · Arrival (relative offset always; wall-clock + date when start set) · Start/Finish (○/⚑) · Actions (Focus on map + Open in Google Maps only). Per-leg travel time/distance/mode is NOT a row column (FR-3). Dropped in trip view: Select checkbox, Coordinates, Collection chips, Added date, per-row Move/Copy/Delete, and the batch-action toolbar. The narrow `w-64` side column is removed.
- FR-3: The **per-leg travel time is shown *between* the two stops it connects** — a compact connector on the shared edge of consecutive rows, not a row column and not a separate full row. The connector carries the leg's mode control (Feature F), travel time ("min"), distance, fidelity badge, and edit/reset affordance (FR-25); an uncomputed/Any leg reads "—". The closing leg (roundtrip return, or leg to a designated Finish) renders after the last row, ahead of the finish/return footer.
- FR-4: **Stop Order is the single canonical ordering for the collection.** Reordering in Trip View (drag, ▲▼, TSP-Sort, MCP) writes the shared `PoiCollectionItem.OrderIndex` (sole-writer `TripOrderingService.SetOrderAsync`), and the **plain Filtered Results list renders in that same order** when an order exists. One ordering entity; the order persists between the two views.
- FR-5: The **map stays visible** beside/above the trip list (two-region work area preserved), and list↔map two-way selection sync keeps working.
- FR-6: Desktop now matches the pattern mobile already uses (`MapPage.razor:160-165`): Trip View replaces the list content rather than adding a parallel list. *[ASSUMPTION] no drag-resizable splitter needed once the list owns the wide region.*

**Feature B — Legible travel-time fidelity**

- FR-7: Each fidelity badge (Estimated / Measured / Manual) explains its meaning in plain language on hover and to AT — replacing the circular "Provenance: Estimated."
- FR-8: When the deployment has no measured provider (default `Mock`, all legs `Estimated`), the panel makes this legible (straight-line estimates, the expected default) — distinct from the engine-unreachable fallback note.
- FR-9: "Recompute travel times" must not imply it will upgrade fidelity when no measured provider is configured.
- FR-10: The panel **recommends enabling OSRM** for measured road times — explains the Estimated state and points the user to how to enable the optional self-hosted OSRM engine (e.g. link to `docs/osrm.md`). It does NOT stand up or configure OSRM.

**Feature C — Clean trip-row layout (residual)**

- FR-11: In the wide trip list, the row columns present as orderly aligned columns, not a ragged cluster.
- FR-12: Row alignment holds across stop-row states: placeable vs. unplaceable, Start/Finish pinned, dwell set vs. empty, long vs. short names. (Leg-level states belong to the connector, FR-3.)

**Feature D — Reconciled travel-time arithmetic & units**

- FR-13: The displayed trip **total** equals the sum of the displayed **per-leg** times — no drift from independent rounding.
- FR-14: Displayed **arrivals** are produced by the existing `ItineraryTimeline` accumulation rule (Start's dwell counts once; each subsequent stop = prior arrival + leg travel + that stop's dwell) and **reconcile** with the displayed per-leg/total figures. Does not redefine accumulation — removes rounding drift.
- FR-15: Rounding is applied **once at the display edge** from canonical seconds, consistently across legs/arrivals/total, preserving honesty qualifiers ("—" uncomputed, Estimated/Measured/Manual provenance, partial-trip em-dash).
- FR-16: The **minute unit renders as "min"**, not "m" ("22 min", "1h 20 min", "<1 min") — disambiguating from distance meters. Hours stay "h"; distance meters stay "m". Changed in `UiStrings.TripDuration*`; canonical seconds unchanged. (Shared layer — both surfaces.)

**Feature E — Discoverable button tooltips**

- FR-17: Every icon-only control in the trip list shows a hover tooltip naming its action: move up/down, Set/Unset Start (○), Set/Unset Finish (⚑), TSP-Sort, Recompute.
- FR-18: Tooltip text comes from `UiStrings` (reusing each control's `aria-label` where apt) and reflects the control's **state** ("Set as Start" vs. "Unset Start"; disabled edge/pinned controls read sensibly). Sighted + AT parity.

**Feature F — Per-leg travel mode (new capability)**

- FR-19: Travel mode is a property of **each leg** (consecutive pair + roundtrip closing leg), not the trip. Each leg shows + lets the user set its mode: Walk / Drive / Cycle / Any-Air.
- FR-20: A **newly appeared** leg (from reorder, TSP-Sort, MCP, add/remove, or any recalc changing consecutive pairs) defaults to **Any/Air ("undefined")**: no auto-computed time, reads "—" until the user acts. "Undefined" and "Any/Air" are the **same state** (no auto time, manual-only); there is no separate "unset" value.
- FR-21: A **ground mode** (Walk/Drive/Cycle) yields an automatic time (Estimated, or Measured under OSRM). **Any/Air legs are never auto-estimated** — time is user-specified only.
- FR-22: A leg **unchanged** across a reorder (same From→To, same mode) **retains** its mode + cached time; only newly appeared pairs reset to Any/Air. (Directional mode-keyed `RouteSegment` cache supports this — TRIP-CACHE-01.)
- FR-23: The **trip-level mode selector is removed** — per-leg modes replace it. Per-leg mode persists per stop's outgoing leg (nullable `PoiCollectionItem.OutgoingTravelMode`); a small EF migration adds it, constrained by the `TravelMode.All` check pattern.
- FR-24: The **per-leg travel mode is reachable from the `map_editor` MCP**: `get_trip` reports each leg's mode, and a tool sets a leg's mode (alongside existing `assign_stop_order` / `set_dwell_time`). Retiring `PoiCollection.TravelMode` (FR-23) would otherwise break the MCP contract.
- FR-25: The **per-leg travel time is user-editable** (inline on the connector or via small popup) and **resettable to the auto value**. Editing sets a **Manual** override (Manual fidelity, never auto-overwritten — TRIP-MANUAL-01); **Reset** clears it and returns the leg to its auto time (Estimated/Measured for ground; "—"/undefined for Any/Air).

**Feature G — Multi-day schedule: start time & time limit (new capability)**

- FR-26: **Start is specified as date AND time** (date-time picker). Persisted in existing `PoiCollection.TripStartTime` (`DateTime?` — no schema change); empty still means relative offsets only. Replaces the `type="time"` + `DateTime.Today` hard-pairing.
- FR-27: Wall-clock arrivals reflect the date and **roll across midnight / multiple days** — a later-day arrival shows its date. Date/time are **locale-driven** (`CultureInfo.CurrentCulture`). Continuous accumulation unchanged (no overnight modeling).
- FR-28: The **time limit** (renamed from "Time budget") is a **fixed goal the user sets**. It can be entered as a **duration** via a time picker (HH:MM), not only raw minutes. Persisted as `TimeBudgetMinutes` (no schema change); HH:MM ↔ minutes at the UI edge. The app shows an **"Over limit"** indicator when the trip total exceeds it.
- FR-29: The time limit can **alternatively** be entered by picking a **finish-by deadline** (date + time): the app computes the limit **once** as `deadline − start` (requires a start). Input convenience only — afterwards a fixed goal stored as minutes; does **not** recompute when start/trip changes. (Distinct from the Finish stop of Feature H.)
- FR-30: **Dwell is entered with a duration picker (HH:MM)**, not a raw-minutes box. Persisted as canonical `DwellMinutes`; empty clears it. No schema change.

**Feature H — Finish designation & roundtrip readout**

- FR-31: A trip is **roundtrip by default**; with no Finish, the end readout reads **"Return to start"** + the return-to-Start arrival.
- FR-32: Pressing **Finish** on a stop makes the trip an **open path**: that stop becomes the Finish, is **pinned to the end of the list**, and the readout switches to **"Finish"** + its arrival time/date (date-aware, FR-27) — never "Return to start" while a Finish is set.
- FR-33: The Finish designation is **revertable**: unsetting returns the trip to roundtrip and the readout to "Return to start," no data loss. (Logic largely exists — primarily verify on running app and fix any gap.)

### NonFunctional Requirements

- NFR1: **Strict layering** — markup-only `.razor` bridge → `TripViewModel` → services. All arithmetic (FR-13–15), HH:MM/date conversions, reconciliation live in the service/VM layer, never the component. Feature A is a markup/layout move reusing `TripStopList`/VM — no new ordering/timeline logic.
- NFR2: **Canonical units fixed at the edges** — travel time in seconds, distance in meters, dwell/budget in minutes; convert only at UI/provider boundaries. Canonical seconds/meters/minutes never converted mid-layer.
- NFR3: **No change to `RouteSegment` cache semantics**, the directional `(From,To,Mode)` key (TRIP-CACHE-01), or the default `Mock` provider.
- NFR4: **Schema discipline** — per-leg mode adds one nullable column (`OutgoingTravelMode`) via a single additive EF migration applied through startup `MigrateAsync`, constrained by the `TravelMode.All` check pattern (TRIP-SCHEMA-01). No new cache shape. Never `EnsureCreated`; never hand-edit an applied migration.
- NFR5: **Cross-surface invariant** — shared-layer changes (FR-16 units; Feature D arithmetic; Feature F data/VM; Feature G persistence) are authored once and apply to **both** desktop and mobile. Mobile data/strings/times must stay correct; don't break `MobileTripPanel`. Only mobile **controls** are deferred to the mirror phase.
- NFR6: **UI conventions** — all new/changed text via `UiStrings`; Tailwind `surface-*` / `on-surface-*` / `primary` tokens only; no group-B analyzer violations; `TreatWarningsAsErrors` holds; no `ConfigureAwait(false)`.
- NFR7: **Accessibility** — preserve `aria-live` / `aria-label` parity; tooltips also available to AT; list↔map sync + keyboard reorder/select intact after the relocation.
- NFR8: **Testing** — cover the desktop component path (bUnit) and the arithmetic invariant (unit). After any Trip VM/DI/schema change, run the Trip integration filter (`FullyQualifiedName~Integration&FullyQualifiedName~Trip`). Add a test asserting Trip-View-on hides the PoiTable and shows the wide stop list. Keep existing mobile trip tests green.
- NFR9: **No regressions** to map-side leg rendering, stop-order badges, selection sync, or per-collection toggle persistence.
- NFR10: **DI seam discipline** — any new Trip VM/service dependency must be registered in **BOTH** `AddTripServices()` overloads (parameterless + `IConfiguration`); the parameterless overload is the recurring integration-host regression point.

### Additional Requirements

_From the Architecture document (RD1–RD13), technical requirements that impact epic/story creation:_

- **No starter template / scaffolding** — brownfield delta on the existing LucidCartographer codebase. The **first implementation story is the `AddOutgoingTravelMode` EF Core migration** (RD1), not a project-init command.
- **RD1a — drop vs keep `PoiCollection.TravelMode`:** recommended to DROP it in the same migration (EF Core 8 SQLite table-rebuild handles `DropColumn`) for cleanliness; fallback is to leave it as a dead column and stop referencing it. Confirm at migration-story time against the live schema.
- **MCP contract migration (RD6):** `get_trip` (`TripTools.GetTrip` → `TripDto`) drops the single trip-wide `travelMode` and adds a per-leg `travelMode` to each leg DTO; a **new tool `set_leg_travel_mode`** identifies the leg by its **From stop** and sets one of `TravelMode.All`. Verb-first naming; rides the unchanged three-tier `/mcp` auth.
- **TSP cost basis is mode-invariant (RD3):** TSP-Sort builds its cost matrix from straight-line/haversine distance (or a fixed nominal ground mode), never from per-leg `OutgoingTravelMode` — decoupling ordering from per-leg display modes. No change to the NN+2-opt algorithm.
- **Per-leg manual reset path (RD7):** reset = delete the leg's cache row then recompute (ground) or leave "—" (Any/Air), under `SqliteWriteLock`; never downgrade a Manual or Measured row.
- **Ground-only auto-compute (RD2):** the background compute pass enqueues a leg iff its mode ∈ {Walk, Drive, Cycle}; AnyAir legs are never auto-estimated.
- **One ordering + mode-reset write-path:** reorder/TSP/MCP write `OrderIndex` through `TripOrderingService.SetOrderAsync` and null `OutgoingTravelMode` ONLY for stops whose successor changed.
- **New `TRIP-*` design codes** to tag this feature's invariants: `TRIP-LEGMODE-01` (From-stop owns the outgoing leg; null ≡ AnyAir), `TRIP-RECONCILE-01` (round-once display model), `TRIP-SCHEDULE-01` (finish-by computed once).
- **No infrastructure/deployment change** — single Blazor Server container + SQLite volume untouched; `Mock` haversine stays default; OSRM remains an opt-in sidecar this feature only *recommends*. The one migration applies on startup.
- **Suggested implementation sequence (RD impact analysis):** (1) RD1 migration → (2) RD5 "min" + RD4 reconciled display (shared-layer, no schema dep, parallelizable) → (3) RD2 leg-projection + RD3 TSP basis → (4) RD6 MCP migration → (5) RD8 takeover + RD9 connector + RD7 manual/reset → (6) RD10 schedule + RD11 fidelity + RD12 tooltips + RD13 finish readout → (7) mirror-to-mobile (deferred).

### UX Design Requirements

_From DESIGN.md (visual specs) and EXPERIENCE.md (behavioral rules). Tokens (colors, typography, spacing, radius, elevation) are **unchanged** — these add specs for the new Trip View components only._

- UX-DR1: **Trip stop row** (`components.trip-stop-row`) — wide, full-width trip-scoped table row (~44px, virtualized rhythm, echoes `{components.table-row}`). Columns per FR-2; selected row = `{colors.primary}/10` tint + inset `{colors.primary}` ring; row click selects (list→map), dwell/action clicks `stopPropagation`.
- UX-DR2: **Inter-row leg connector** (`components.leg-connector`) — thin single-line strip on the shared border between two stop rows, inset to align under the name column, `{surface-container}` background with hairline dividers. Carries: `↓` glyph · mode pill · travel time ("min") · `·` distance · fidelity badge · reset (↺) hidden at rest, revealed on hover/focus. Closing leg renders as the same connector after the last row. *[ASSUMPTION] left-indent under name; confirm at mock.*
- UX-DR3: **Per-leg mode pill** (`components.leg-mode-pill`) — rounded-full pill, `text-xs`. Set state: `{colors.primary}/10` fill + `{colors.primary}` text + mode glyph + label. Undefined (Any): outline only, label "Any — set mode" + help glyph (neutral, NOT an error color). Click opens a Material list menu of the four modes (Walk/Drive/Cycle/Any-Air) with the active one checked. Replaces the per-trip selector.
- UX-DR4: **Schedule pickers** (`components.schedule-picker`) — native, token-styled inputs (no bespoke calendar): Start = `datetime-local`; Time limit = `time` (HH:MM) with alternate `datetime-local` for the finish-by deadline; Dwell = `time` (HH:MM). Inherited input chrome (`{surface-container-low}` fill, hairline border, focus ring `{colors.primary}`).
- UX-DR5: **Fidelity badge** — self-explaining hover/AT tooltip in plain language ("Estimated — straight-line approximation, not road distance"). When all legs are default-`Mock` Estimated, a quiet contextual note explains the state and **recommends enabling OSRM** (links to `docs/osrm.md`) — distinct from the engine-unreachable fallback note. Badge/line visuals unchanged.
- UX-DR6: **Leg-time inline edit + reset** — clicking the connector's time turns it into an inline editable field; entering a value sets a Manual override (Manual fidelity). The reset (↺), shown on hover/focus only, clears the override to the auto value. Generalizes manual entry to any leg.
- UX-DR7: **Start / Finish footer readout** — roundtrip default → footer "Return to start" + return arrival; pressing Finish pins the stop to the end and flips the footer to "Finish" + that stop's date-aware arrival; revertable (unset → roundtrip), never both.
- UX-DR8: **"Over limit" chip** — amber soft-warn (`text-amber-600` on `{surface-container}`), never `{colors.tertiary}`/red. Informational, non-blocking; absent when no limit is set.
- UX-DR9: **Microcopy / voice** — fidelity tooltips ("Estimated…/Measured…/Manual…"); Mock-default note ("All times are straight-line estimates. Enable OSRM for measured road times." + link); undefined-leg pill "Any — set mode"; overrun chip "Over limit"; field labels "Time limit", "Start", "Dwell". All via `UiStrings`.
- UX-DR10: **Accessibility floor** — every icon-only control gets a `title` at parity with its `aria-label` (move/start/finish/TSP/Recompute); native pickers keep keyboard + SR support; reset (↺) is a real focusable button with an `aria-label`; keyboard reorder (▲▼) and list↔map sync intact after the takeover.
- UX-DR11: **Undefined / Any leg state** — a newly-appeared leg shows "—" with the "Any — set mode" pill (awaiting a mode, distinct from *computing*); no auto time until a ground mode chosen or manual time entered; styled as a neutral outline, never an error.
- UX-DR12: **Multi-day rollover state** — an arrival on a later calendar day than the start shows its date alongside the time (locale-driven), so a multi-day trip reads on its real days.

### FR Coverage Map

- FR-1: Epic 1 — Trip View on switches the wide list region into the trip (PoiTable hidden)
- FR-2: Epic 1 — Wide trip-scoped table with full columns; side column + batch toolbar removed
- FR-3: Epic 1 — Per-leg info on the inter-row connector (created here; pill/edit added in Epic 3)
- FR-4: Epic 1 — Stop Order is the single canonical ordering; plain list follows it
- FR-5: Epic 1 — Map stays visible; list↔map two-way sync preserved
- FR-6: Epic 1 — Desktop matches mobile's switch pattern; no resizable splitter
- FR-7: Epic 2 — Self-explaining fidelity badges (Estimated/Measured/Manual)
- FR-8: Epic 2 — Legible "all straight-line estimates" state for default Mock
- FR-9: Epic 2 — Recompute copy doesn't imply a fidelity upgrade without a measured provider
- FR-10: Epic 2 — Panel recommends enabling OSRM (links docs/osrm.md); does not configure it
- FR-11: Epic 1 — Orderly aligned trip-row columns (no ragged cluster)
- FR-12: Epic 1 — Row alignment holds across stop-row states
- FR-13: Epic 2 — Displayed total == sum of displayed per-leg times
- FR-14: Epic 2 — Arrivals from existing ItineraryTimeline rule, reconciled with figures
- FR-15: Epic 2 — Round once at the display edge; honesty qualifiers preserved
- FR-16: Epic 2 — Minute unit "m" → "min" (UiStrings.TripDuration*)
- FR-17: Epic 2 — Hover tooltips on every icon-only trip control
- FR-18: Epic 2 — Tooltip text from UiStrings, state-reflecting, at aria-label parity
- FR-19: Epic 3 — Travel mode is per-leg (Walk/Drive/Cycle/Any-Air), not per-trip
- FR-20: Epic 3 — Newly-appeared legs default to Any/Air "—" (null ≡ AnyAir, one state)
- FR-21: Epic 3 — Ground modes auto-time; Any/Air never auto-estimated
- FR-22: Epic 3 — Unchanged legs retain mode + cached time across reorder
- FR-23: Epic 3 — Trip-level mode selector removed; nullable OutgoingTravelMode column added
- FR-24: Epic 3 — MCP get_trip per-leg mode + new set_leg_travel_mode tool
- FR-25: Epic 3 — Per-leg time user-editable (Manual) + resettable to auto
- FR-26: Epic 4 — Start specified as date AND time (TripStartTime, no schema change)
- FR-27: Epic 4 — Date-aware arrivals roll across midnight/days; locale-driven
- FR-28: Epic 4 — Time limit (renamed) entered as HH:MM duration; "Over limit" indicator
- FR-29: Epic 4 — Time limit alternatively via finish-by deadline, computed once
- FR-30: Epic 4 — Dwell entered with HH:MM duration picker (canonical DwellMinutes)
- FR-31: Epic 4 — Roundtrip default → "Return to start" + return arrival
- FR-32: Epic 4 — Finish pins stop to end; readout flips to "Finish" + dated arrival
- FR-33: Epic 4 — Finish designation revertable to roundtrip, no data loss

## Epic List

### Epic 1: Readable Trip View takeover (desktop)
Toggling Trip View switches the desktop filtered-results region into a full-width
trip table — full POI names, clean aligned rows, per-leg travel info on an inter-row
connector — with the map and list↔map selection sync preserved and the plain list
following the canonical Stop Order. Fixes the root reported divergence (the cramped
256px side column beside an unchanged PoiTable, names truncated to numbers).
**FRs covered:** FR-1, FR-2, FR-3, FR-4, FR-5, FR-6, FR-11, FR-12

### Epic 2: Trustworthy & legible trip times
Make the trip's numbers honest and readable: the displayed total equals the sum of
the displayed per-leg times, arrivals reconcile, the minute unit reads "min" (no
collision with distance "m"), fidelity badges self-explain with an "all estimates /
enable OSRM" note, and every icon-only control reveals what it does on hover. These
shared-layer fixes reach both desktop and mobile by nature.
**FRs covered:** FR-7, FR-8, FR-9, FR-10, FR-13, FR-14, FR-15, FR-16, FR-17, FR-18

### Epic 3: Honest per-leg travel modes
Each movement between two stops carries its own travel type (Walk / Drive / Cycle /
Any-Air), replacing the single trip-wide mode — drive into town, walk between stops,
type a flight time manually; a newly-appeared leg awaits a mode ("—") and is never
silently timed as a walk. Reachable from the map_editor MCP so AI-assigned trips can
set modes. Carries the feature's one schema migration as its first story.
**FRs covered:** FR-19, FR-20, FR-21, FR-22, FR-23, FR-24, FR-25

### Epic 4: Multi-day schedule & honest finish
The trip anchors to a real date+time start, arrivals roll across days showing their
dates, a Time limit can be entered as an HH:MM duration or a finish-by deadline (with
an "Over limit" warn), dwell uses an HH:MM picker, and a designated Finish reads
"Finish" + its dated arrival instead of the roundtrip "Return to start" — revertably.
**FRs covered:** FR-26, FR-27, FR-28, FR-29, FR-30, FR-31, FR-32, FR-33

---

## Epic 1: Readable Trip View takeover (desktop)

When a collection is viewed as a trip, the desktop filtered-results region *becomes*
the trip stop list — a full-width, readable table — instead of bolting a cramped
256px side column beside an unchanged PoiTable. The map and list↔map selection sync
stay intact, and the plain list follows the same canonical Stop Order. This epic
fixes the root reported divergence (names truncated to "numbers without names").

### Story 1.1: Switch the desktop list region into the trip

As a trip planner on desktop,
I want toggling Trip View on to replace the POI table with the trip stop list in the
same wide region (and toggling off to restore the table unchanged),
So that I see one trip-focused list instead of two redundant lists in a cramped column.

**Acceptance Criteria:**

**Given** a single collection with ≥2 placeable POIs is in scope and Trip View is off
**When** I toggle Trip View on
**Then** the desktop filtered-results region renders `TripStopList` instead of `PoiTable`
**And** the plain `PoiTable` is not shown at the same time
**And** the additive `w-64` side column is removed
**And** the map stays visible in its region and list↔map two-way selection sync still works

**Given** Trip View is on
**When** I toggle Trip View off
**Then** the plain `PoiTable` and its controls are restored exactly as before, with no data loss
**And** per-collection toggle persistence still behaves as it did (no regression, NFR9)

**Given** the desktop takeover is implemented
**When** the bUnit component test for `MapPage` runs
**Then** it asserts Trip-View-on hides `PoiTable` and shows the wide `TripStopList` (NFR8)
**And** the change is markup/Tailwind in `MapPage.razor` reusing the existing `TripStopList`/VM, with no new ordering or timeline logic (NFR1)

**Given** the shared `TripViewModel` is used by both surfaces
**When** the Trip integration filter runs after this change
**Then** the desktop takeover flow passes and existing mobile trip tests stay green (NFR5, NFR8)

### Story 1.2: Wide trip-scoped table with trip-only columns

As a trip planner on desktop,
I want the trip list shown as a wide table with full POI names and only trip-relevant
columns,
So that I can read each stop clearly without the collection-management clutter that no
longer applies in a trip.

**Acceptance Criteria:**

**Given** Trip View is on
**When** the trip stop table renders
**Then** each row shows, left→right: a reorder gutter (drag handle **and** ▲▼ move buttons),
the Stop # badge (with Start/Finish glyph + ring), the **full POI name** with address
sub-line and enrichment-state icon, a Dwell input, an Arrival cell (relative offset
always), Start/Finish (○/⚑) controls, and Actions (**Focus on map** + **Open in Google
Maps** only)
**And** the POI name is not truncated to an unreadable width (UX-DR1)

**Given** the trip stop table renders
**When** I compare it to the plain PoiTable
**Then** the Select checkbox, Coordinates, Collection chips, Added date, and per-row
Move/Copy/Delete actions are absent
**And** the batch-action toolbar (Select all / Move / Copy / Delete selected) above the
list is absent
**And** the list header carries only trip-relevant controls (stop count, TSP-Sort,
Recompute, total travel time, start, time limit); Fit All / Labels stay on the map

**Given** a row is clicked (not on a dwell/action control)
**When** the click is handled
**Then** the stop is selected (list→map), and dwell/action clicks `stopPropagation` so they
do not also select the row (UX-DR1)

**Given** varied stop-row states (placeable vs. unplaceable, Start/Finish pinned, dwell set
vs. empty, long vs. short names)
**When** the table renders any combination of them
**Then** the columns stay orderly and aligned — not a ragged cluster (FR-11, FR-12)
**And** a selected row shows the `{colors.primary}/10` tint + inset `{colors.primary}` ring
(UX-DR1)

### Story 1.3: Inter-row leg connector

As a trip planner,
I want each leg's travel time shown on the boundary between the two stops it connects,
So that travel information reads as "between these stops" rather than crowding a row column.

**Acceptance Criteria:**

**Given** the trip stop table renders consecutive stops
**When** a leg between two stops exists
**Then** a compact single-line `LegConnector` appears on the shared edge between the two
rows, inset to align under the name column (UX-DR2)
**And** it shows the `↓` glyph, travel time (in "min" units), `·` distance, and the fidelity
badge — and a reset (↺) affordance hidden at rest, revealed on hover/focus
**And** per-leg travel time/distance is **not** rendered as a stop-row column (FR-3)

**Given** the closing leg of the trip (roundtrip return, or the leg to a designated Finish)
**When** the table renders
**Then** the closing connector renders after the last row, ahead of the finish/return footer

**Given** a leg has no computed time yet (uncomputed)
**When** the connector renders
**Then** the travel time reads "—" and the connector is styled neutrally (not as an error,
UX-DR11)

**Given** the connector is a presentational component under `Components/Shared/Trip/`
**When** it renders
**Then** it is driven by the VM's leg projection and raises no service calls or state
mutations itself (NFR1)
**And** the mode pill and generalized edit/reset are deferred to Epic 3 (this story builds the
connector shell with time/distance/fidelity/reset)

### Story 1.4: Canonical Stop Order shared across both views

As a trip planner,
I want the order I set in Trip View to be the one order for the collection, reflected in the
plain list too,
So that the trip and the plain list never disagree about stop sequence.

**Acceptance Criteria:**

**Given** I reorder stops in Trip View (drag, ▲▼, TSP-Sort, or via MCP)
**When** the reorder is committed
**Then** it writes the shared `PoiCollectionItem.OrderIndex` through
`TripOrderingService.SetOrderAsync` (the sole writer, under `SqliteWriteLock`)
**And** no other code path writes `OrderIndex`

**Given** a collection has an explicit Stop Order
**When** I view the plain Filtered Results list (Trip View off)
**Then** the plain list renders in that same Stop Order

**Given** a collection that has never been put into Trip View / has no explicit order
**When** I view the plain list
**Then** it keeps its normal default sort (no forced ordering)

**Given** I set an order in one view and switch to the other
**When** the other view renders
**Then** the order persists between the two views with no divergence (FR-4)
**And** stop-order badges and selection sync remain correct (NFR9)

---

## Epic 2: Trustworthy & legible trip times

Make the trip's numbers honest and readable across both surfaces: the displayed total
equals the sum of the displayed per-leg times, arrivals reconcile with those figures,
the minute unit reads "min" (no collision with distance "m"), fidelity badges
self-explain, and every icon-only control reveals its action on hover. These are
shared-layer changes (`ItineraryTimeline`, `TravelTimeFormatting`, `UiStrings`,
`FidelityBadge`, `TripViewModel`) authored once — they reach mobile by nature, so
`MobileTripPanel` must stay correct.

### Story 2.1: Reconciled travel-time arithmetic (round-once display model)

As a trip planner,
I want the per-leg times, arrivals, and trip total to agree with each other,
So that I can trust the schedule instead of seeing legs sum to one number while the total
shows another.

**Acceptance Criteria:**

**Given** a trip with computed legs
**When** the timeline produces its display model
**Then** each leg is rounded **once** from canonical seconds to whole minutes (nearest minute,
sub-minute non-zero → "<1 min"), and BOTH the cumulative arrivals AND the trip total are
derived from those same rounded per-leg minutes (+ integer dwell) — TRIP-RECONCILE-01

**Given** the display model is rendered
**When** I read the figures
**Then** the displayed trip total equals the sum of the displayed per-leg times (FR-13)
**And** the displayed arrivals follow the existing `ItineraryTimeline` accumulation rule (Start
dwell counts once; each subsequent stop = prior arrival + leg travel + that stop's dwell) and
reconcile with the displayed per-leg/total figures (FR-14)

**Given** a leg is uncomputed or Any/Air
**When** the display model is produced
**Then** that leg contributes "—" and the total shows the partial-trip em-dash (no silent zero),
and the mixed-fidelity aggregate behaviour is preserved (FR-15)

**Given** the canonical accumulation is unchanged
**When** arithmetic runs
**Then** it lives in the service/VM layer (`ItineraryTimeline` / `TravelTimeFormatting` /
`TripViewModel`), never in the `.razor` component (NFR1)
**And** a unit test asserts the reconciliation invariant (total == Σ displayed legs; arrivals
reconcile; partial-trip "—" and engine-unreachable fallback intact), and the Trip integration
filter stays green (NFR8)

### Story 2.2: Minute unit reads "min"

As a trip planner,
I want durations shown as "min" rather than "m",
So that I don't confuse "22m" (minutes) with "397 m" (distance meters).

**Acceptance Criteria:**

**Given** the duration strings in `UiStrings.TripDuration*`
**When** a duration is formatted
**Then** minutes render as "min" ("22 min", "1h 20 min", "<1 min"); hours stay "h"; distance
meters stay "m" (FR-16)

**Given** the change is in shared `UiStrings`
**When** either surface renders durations
**Then** desktop and mobile both show "min" (shared layer, NFR5)
**And** canonical seconds are unchanged; no literal duration text appears outside `UiStrings`
(NFR6)

### Story 2.3: Self-explaining fidelity badges

As a trip planner,
I want each fidelity badge to explain what it means in plain language,
So that I understand "Estimated" / "Measured" / "Manual" without a circular "Provenance:
Estimated" tooltip.

**Acceptance Criteria:**

**Given** a leg's fidelity badge
**When** I hover it (or read it via AT)
**Then** it shows a plain-language explanation: "Estimated — straight-line approximation, not
road distance" / "Measured — real road route." / "Manual — you entered this time." (FR-7,
UX-DR5/DR9)

**Given** the tooltip text
**When** it is rendered
**Then** it comes from `UiStrings` and is available to assistive technology at parity with the
visible text (NFR6, NFR7)
**And** the badge/line visuals are otherwise unchanged

### Story 2.4: Mock-default estimate note & OSRM recommendation

As a trip planner on a default deployment,
I want the panel to tell me why every leg is "Estimated" and how to get measured times,
So that I understand the state and know the optional path to road-accurate times.

**Acceptance Criteria:**

**Given** the deployment has no measured provider (default `Mock`) and all legs are non-fallback
`Estimated`
**When** the trip panel renders
**Then** a quiet contextual note explains the state ("All times are straight-line estimates…")
and **recommends enabling OSRM**, linking to `docs/osrm.md` (FR-8, FR-10, UX-DR5/DR9)
**And** this note is distinct from the existing engine-unreachable fallback note (which keeps
meaning "we tried to measure and couldn't")

**Given** no measured provider is configured
**When** I read the "Recompute travel times" control and its copy
**Then** the copy does not imply that recomputing will upgrade fidelity (FR-9)

**Given** this PRD does not stand up OSRM (Non-Goal)
**When** the recommendation is shown
**Then** it only guides the operator (link/explanation); it does not configure or enable OSRM
**And** all copy is sourced from `UiStrings` (NFR6)

### Story 2.5: Discoverable icon-button tooltips

As a sighted trip planner,
I want every icon-only control to reveal what it does on hover,
So that I get the same affordance screen-reader users already have from `aria-label`.

**Acceptance Criteria:**

**Given** the trip list's icon-only controls (move up/down, Set/Unset Start ○, Set/Unset
Finish ⚑, TSP-Sort, Recompute)
**When** I hover any of them
**Then** a native `title` tooltip names the action (FR-17), matching the drag-handle precedent

**Given** a control with state
**When** its tooltip renders
**Then** the text reflects the control's state ("Set as Start" vs "Unset Start"), disabled
edge/pinned controls read sensibly, and the text is at parity with the existing `aria-label`
(FR-18, UX-DR10)
**And** tooltip text is sourced from `UiStrings`, reusing each control's `aria-label` where apt
(NFR6, NFR7)

---

## Epic 3: Honest per-leg travel modes

Each movement between two stops carries its own travel type (Walk / Drive / Cycle /
Any-Air), replacing the single trip-wide mode. A newly-appeared leg awaits a mode
("—") and is never silently timed as a walk; ground modes auto-time, Any/Air is
manual-only. Per-leg mode is reachable from the `map_editor` MCP so AI-assigned trips
keep working. This epic carries the feature's one schema migration (its first story)
and builds the per-leg mode spine end-to-end: data → projection → compute → UI → MCP.

### Story 3.1: Add the per-leg travel-mode column (migration)

As the system,
I want a per-leg travel-mode column on the stop membership,
So that each leg's mode can be stored without a separate leg entity.

**Acceptance Criteria:**

**Given** the EF model
**When** the `AddOutgoingTravelMode` migration is created and applied via startup `MigrateAsync`
**Then** `PoiCollectionItem` gains a nullable `OutgoingTravelMode` (string, one of
`TravelMode.All` = {AnyAir, Drive, Walk, Cycle}) — the mode of the leg leaving this stop —
constrained by the `TravelMode.All` check pattern (TRIP-SCHEMA-01)
**And** `null` is semantically identical to AnyAir (one "undefined / Any-Air" state — TRIP-
LEGMODE-01); no separate "unset" sentinel is introduced

**Given** `PoiCollection.TravelMode` no longer drives legs (FR-23)
**When** the migration runs
**Then** it drops `PoiCollection.TravelMode` (RD1a recommended; fallback: leave as a dead,
unreferenced column if the SQLite table-rebuild proves risky — decided at this story's time)

**Given** schema discipline
**When** the migration is authored
**Then** it is a single additive migration applied through `MigrateAsync`; `EnsureCreated` is not
used and no applied migration is hand-edited (NFR4)
**And** the Trip integration filter runs and stays green after the schema change (NFR8)

### Story 3.2: Per-leg mode projection & ground-only auto-compute

As a trip planner,
I want each leg's time to come from its own mode,
So that a Drive leg and a Walk leg are timed differently instead of all the same.

**Acceptance Criteria:**

**Given** the trip is projected
**When** `TripViewModel.BuildLegs` runs
**Then** it reads each leg's `OutgoingTravelMode` (from the From-stop membership) and looks the
leg up in the cache by its own directional `(From, To, mode)` key (TRIP-CACHE-01); `TripLeg`
carries a `Mode` field (FR-19)

**Given** a leg's mode is a ground mode (Walk / Drive / Cycle)
**When** the background compute pass runs
**Then** it enqueues that leg and yields an automatic time (Estimated, or Measured under OSRM)
(FR-21)

**Given** a leg's mode is Any/Air (incl. null)
**When** the compute pass runs
**Then** that leg is **never** auto-estimated and reads "—" until a mode/manual time is set
(FR-21, TRIP-LEGMODE-01)

**Given** this is data + projection + compute (no new VM/service ctor dependency)
**When** the change lands
**Then** the `AddTripServices()` overload pair is untouched; if any dependency is nonetheless
added it is registered in BOTH overloads and the Trip integration filter is re-run (NFR10, NFR8)

### Story 3.3: Reset newly-appeared legs on reorder; keep TSP mode-invariant

As a trip planner,
I want reordering to reset only the legs that actually changed and to never optimise on
per-leg modes,
So that unchanged legs keep their mode and time, and ordering doesn't deadlock on modes that
don't exist yet.

**Acceptance Criteria:**

**Given** I reorder stops (drag / ▲▼ / TSP-Sort / MCP)
**When** `TripOrderingService.SetOrderAsync` commits the new order
**Then** it nulls `OutgoingTravelMode` **only** for stops whose successor changed; a leg whose
`(From→To)` pair is unchanged retains its mode and cached time (FR-20, FR-22)
**And** newly-appeared legs default to Any/Air and read "—" with the "Any — set mode" pill
(FR-20, UX-DR11)

**Given** TSP-Sort must order stops before per-leg modes exist
**When** it builds its cost matrix
**Then** the matrix uses a mode-invariant basis (straight-line/haversine distance, or a fixed
nominal ground mode), never per-leg `OutgoingTravelMode` (RD3); the NN+2-opt algorithm itself is
unchanged
**And** after ordering, the resulting newly-appeared legs default to Any/Air per the reset rule

**Given** the order + mode-reset write-path
**When** any reorder runs
**Then** `OrderIndex` and `OutgoingTravelMode` are mutated only through `TripOrderingService`
under `SqliteWriteLock` (no other writer)

### Story 3.4: Per-leg mode pill on the connector

As a trip planner,
I want a mode control on each leg's connector instead of one trip-wide selector,
So that I can set Walk / Drive / Cycle / Any-Air for each movement individually.

**Acceptance Criteria:**

**Given** a leg connector
**When** it renders
**Then** it shows a `LegModePill` displaying the leg's mode ("Drive") when set, or "Any — set
mode" as a neutral outline pill (not an error colour) when undefined (FR-19, UX-DR3/DR11)

**Given** I click the mode pill
**When** the menu opens
**Then** it offers Walk / Drive / Cycle / Any-Air with the active mode checked; choosing a ground
mode triggers compute for that leg, choosing Any/Air leaves it manual-only (FR-19, FR-21)

**Given** per-leg modes replace the trip-wide mode
**When** the trip panel renders
**Then** the trip-level mode selector is removed entirely (no dead duplicate) (FR-23)
**And** the pill is presentational — it raises a VM command and never mutates state or calls
services directly (NFR1); all labels come from `UiStrings` (NFR6)

### Story 3.5: Per-leg manual time edit & reset

As a trip planner,
I want to type a leg's time and later reset it to the automatic value,
So that I can record a flight/train time the app can't estimate and undo it cleanly.

**Acceptance Criteria:**

**Given** any leg (ground or Any/Air)
**When** I click the connector's travel time and enter a value
**Then** it sets a Manual override: a `RouteSegment` row at `Fidelity = Manual`, never
auto-overwritten and never deleted by invalidation (TRIP-MANUAL-01, FR-25, UX-DR6)

**Given** a leg with a Manual override
**When** I use the reset (↺), shown on hover/focus only
**Then** the override is cleared and the leg returns to its auto value: Estimated/Measured for a
ground mode (delete the cache row then recompute under `SqliteWriteLock`), or "—"/undefined for
Any/Air (FR-25)

**Given** the manual/reset write path
**When** it runs
**Then** it stays inside the Trip slice and never downgrades a Manual or Measured row
(TRIP-MANUAL-01); results surface via `StateChanged`, not direct mutation (NFR1)

### Story 3.6: MCP per-leg travel mode (get_trip + set_leg_travel_mode)

As an AI assistant using the `map_editor` MCP,
I want to read and set each leg's travel mode,
So that AI-assigned trips can choose modes instead of being stranded at Any.

**Acceptance Criteria:**

**Given** `get_trip` (`TripTools.GetTrip` → `TripDto`)
**When** it returns a trip
**Then** each leg DTO carries its own `travelMode` (camelCase JSON) alongside the existing
seconds/meters/fidelity, and the single trip-level `travelMode` field is removed (FR-24)

**Given** a new tool `set_leg_travel_mode`
**When** it is called with a From-stop `PoiId` and one of `TravelMode.All`
**Then** it sets that leg's `OutgoingTravelMode` (leg keyed by its From stop, mirroring
`set_dwell_time`); a ground mode triggers compute, AnyAir leaves it manual-only (FR-24)
**And** the tool name is verb-first per the existing `TripTools` convention and rides the
unchanged three-tier `/mcp` auth

**Given** the MCP contract change
**When** `TripToolsTests` run
**Then** they assert `get_trip` per-leg mode and `set_leg_travel_mode` behaviour, and the
Epic-3 AI-assignment flow still round-trips (FR-24, NFR8)

---

## Epic 4: Multi-day schedule & honest finish

A trip spans days. The start is a real date+time, arrivals roll across days showing
their dates, the Time limit (renamed from "Time budget") can be set as an HH:MM
duration or a finish-by deadline (with an "Over limit" warn), dwell uses an HH:MM
picker, and a designated Finish reads "Finish" + its dated arrival instead of the
roundtrip "Return to start" — revertably. All conversions happen at the UI edge; no
schema change (the persisted fields already exist).

### Story 4.1: Date + time start picker

As a multi-day trip planner,
I want to set the start as a date and a time,
So that a "4–7.06.2026" trip can anchor to a real day instead of just a time of day.

**Acceptance Criteria:**

**Given** the start control
**When** I set the trip start
**Then** it is a native `datetime-local` (date AND time) writing the existing
`PoiCollection.TripStartTime` (`DateTime?`); the `type="time"` + `DateTime.Today` hard-pairing
is replaced (FR-26, UX-DR4)

**Given** no start is set (empty)
**When** the trip renders
**Then** arrivals show relative offsets only (unchanged behaviour) (FR-26)

**Given** no schema change is needed
**When** the start is persisted
**Then** it uses the existing `DateTime?` field; conversion/formatting is at the UI edge only
(NFR2); the input chrome uses the inherited token styling (UX-DR4)

### Story 4.2: Date-aware multi-day arrivals

As a multi-day trip planner,
I want arrivals that cross midnight to show their date,
So that a trip reads on its real days instead of wrapping silently.

**Acceptance Criteria:**

**Given** a start date+time is set
**When** arrivals are computed and displayed
**Then** wall-clock arrivals roll across midnight / multiple days, and an arrival on a later
calendar day than the start shows its date alongside the time (FR-27, UX-DR12)

**Given** date/time formatting
**When** an arrival renders
**Then** it is locale-driven (`CultureInfo.CurrentCulture`) with no hard-coded order (FR-27)

**Given** accumulation semantics
**When** arrivals roll across days
**Then** continuous accumulation is unchanged (no overnight "stop for the night" modeling); only
formatting changes, at the UI edge (NFR2)
**And** the formatting lives in `TravelTimeFormatting`/VM (shared layer), keeping mobile correct
(NFR1, NFR5)

### Story 4.3: Time limit as duration or finish-by deadline, with "Over limit"

As a trip planner,
I want to set how long the whole trip should take — as a length or a "done by" deadline — and be
warned when I exceed it,
So that I can keep a multi-day plan within a goal I actually think in.

**Acceptance Criteria:**

**Given** the time-limit control (renamed from "Time budget" to "Time limit"; overrun "Over
budget" → "Over limit" in `UiStrings`)
**When** I enter a limit as an HH:MM duration
**Then** it persists as the canonical `TimeBudgetMinutes` (HH:MM ↔ minutes at the UI edge only);
no schema change (FR-28, NFR2)

**Given** I instead pick a finish-by deadline (date+time, requires a start)
**When** the limit is set
**Then** the app computes it **once** as `deadline − start` and stores it as the fixed-goal
`TimeBudgetMinutes`; it does **not** recompute when the start or the trip later changes
(TRIP-SCHEDULE-01, FR-29)

**Given** a limit is set and the trip total exceeds it
**When** the panel renders
**Then** an "Over limit" indicator shows as an amber soft-warn (`text-amber-600`), never red /
`{colors.tertiary}`; it is informational and non-blocking, and absent when no limit is set
(FR-28, UX-DR8)
**And** the finish-by deadline is distinct from the Finish stop of Story 4.5 (a time goal, not an
end POI) (FR-29)

### Story 4.4: Dwell HH:MM picker

As a trip planner,
I want to enter dwell time as HH:MM,
So that setting "1h 30m at the museum" reads naturally instead of typing raw minutes.

**Acceptance Criteria:**

**Given** the dwell control on a stop row
**When** I enter a dwell
**Then** it is a native HH:MM duration picker writing the canonical `DwellMinutes`; an empty
value clears it; no schema change (FR-30, UX-DR4)

**Given** the conversion
**When** dwell is read/written
**Then** HH:MM ↔ minutes happens only at the UI edge; canonical `DwellMinutes` is unchanged
(NFR2)

### Story 4.5: Finish designation & roundtrip readout

As a trip planner,
I want to designate a final stop and have the readout say "Finish" with its arrival,
So that an open-path trip doesn't misreport as "Return to start."

**Acceptance Criteria:**

**Given** a trip with no Finish designated
**When** the footer renders
**Then** it reads "Return to start" + the return-to-Start arrival (roundtrip default, FR-31)

**Given** I press Finish on a stop
**When** the designation is applied
**Then** that stop becomes the Finish, is pinned to the end of the list (order N), and the footer
switches to "Finish" + that stop's arrival time/date (date-aware per Story 4.2) — never "Return
to start" while a Finish is set (FR-32, UX-DR7)

**Given** a Finish is designated
**When** I unset it
**Then** the trip reverts to roundtrip and the footer to "Return to start," with no data loss
(FR-33)

**Given** the switch logic largely exists today (`IsRoundtrip => FinishPoiId is null`, Finish
pins to N)
**When** this story is implemented
**Then** the behaviour is verified on the running app, any reported misbehaviour is fixed, and
finish/return readout is covered by tests; all copy comes from `UiStrings` (NFR6, NFR8)
