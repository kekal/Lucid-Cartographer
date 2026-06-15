---
title: "Trip View — Layout Realignment & Honest Schedule PRD"
status: final
created: 2026-06-15
updated: 2026-06-15
---

# Trip View — Layout Realignment & Honest Schedule PRD

## 1. Overview

The Trip Planning feature (Epics 1–4, shipped) lets a collection be viewed as an
ordered trip. Using it on a real 5-stop Wrocław trip surfaced one **root layout
divergence** plus **seven secondary issues** spanning legibility, correctness,
and missing capability.

Root issue: on desktop, Trip View was meant to *switch* the collection's list
region into the trip, but it shipped as a **redundant, cramped 256px side column**
beside an unchanged POI table, truncating names to "numbers without names."

This PRD does three things: (1) **realigns the desktop layout** so the trip list
takes over the wide region; (2) fixes **shared correctness/legibility** gaps
(travel-time arithmetic, the minute unit, fidelity legibility, the finish
readout); and (3) adds two **new capabilities** the trip genuinely needs to be
honest — **per-leg travel mode** (Feature F) and a **multi-day schedule**
(start date+time, finish, flexible budget — Feature G). It is therefore a
**moderate, multi-epic** change, not a quick polish pass; Feature F carries a
small schema migration.

### Scope & sequencing (desktop now, mirror to mobile later)

- **Desktop UI is built now.** All layout/control work targets the desktop
  `TripStopList` + `MapPage` surface.
- **Shared-layer fixes reach both surfaces by nature.** Several requirements live
  in code mobile also runs — `UiStrings` (minute unit), `ItineraryTimeline` /
  `TravelTimeFormatting` (arithmetic), `TripViewModel` + entities (per-leg mode,
  schedule). Changing these once keeps mobile's **data, strings, and times
  consistent**; they cannot be "desktop-only."
- **Mobile-specific UI surfacing is a follow-up "mirror to mobile" phase.** The
  mobile panel's controls for the new features (per-leg mode, the inter-row
  connector, date/budget/dwell pickers, tooltips) are deferred to that phase.
  Mobile already performs the layout *switch* (`MapPage.razor:160-165`), so it
  needs no realignment.

## 2. Problem Statement

Observed on the running app (`localhost:5087`, Walk mode, 5 stops):

1. **(Root) Trip View adds a redundant cramped panel instead of switching the
   view.** The spec intended the toggle to turn the collection's list into the
   trip — *FR-1/§4.1 (archived PRD):* ON → "renders as a Trip"; OFF → "the same
   POI set and controls as the pre-feature Collection view"; *EXPERIENCE.md:*
   "switches the visible collection between plain and trip view"; *DESIGN.md:*
   each stop row shows the **POI name**. What shipped
   ([MapPage.razor:326](../../../../LucidCartographer/Components/Pages/MapPage.razor) + `:349`):
   the **PoiTable stays** *and* a separate **`w-64` TripStopList** is bolted on —
   two lists of the same POIs, names truncated. Mobile already switches correctly
   (`MapPage.razor:160-165`); only **desktop** diverged.

2. **The "Estimated" label is meaningless and never changes.** A bare badge with
   a circular tooltip ("Provenance: Estimated"). The default provider is `Mock`
   (`appsettings.json:56`, haversine) so **every leg is permanently `Estimated`**;
   `Measured` only appears under opt-in self-hosted OSRM (not deployed). The one
   explanatory note fires only on engine-unreachable *fallback*, never on the
   default trip.

3. **Travel-time numbers don't reconcile (correctness bug).** Per-leg times sum
   to 78 min while the total reads 1h 20 min (80); arrival stamps imply legs
   ±1–2 min off the per-leg labels. Each value is rounded independently of the
   cumulative figures.

4. **Icon buttons have no tooltips.** Move (▲▼), Set Start (○), Set Finish (⚑),
   TSP-Sort, Recompute expose an `aria-label` but no `title` — no hover hint for
   sighted users; only the drag handle has one.

5. **Travel mode is trip-wide, not per-movement.** One mode
   (`PoiCollection.TravelMode`) times every leg the same — meaningless for a mixed
   trip (drive into town, then walk between stops). The data model already
   supports per-leg modes (`RouteSegment.TravelMode` is in the cache key); only
   the per-trip field forces uniformity. (v1 deliberately scoped single-mode.)

6. **Start time is time-of-day only, no date.** The input is `type="time"` and
   the handler hard-pairs `DateTime.Today`, so a multi-day trip (collections named
   "4-7.06.2026") can't anchor to a real date or roll arrivals into later days.
   `PoiCollection.TripStartTime` is already `DateTime?` — only the UI drops the date.

7. **Time budget is raw minutes only.** No way to enter it as a duration (HH:MM)
   or as an absolute finish date+time ("done by <date/time>").
   `TimeBudgetMinutes` stays canonical; the gap is the input affordances.

8. **The "Return to start" readout doesn't reflect a designated Finish.** It
   reads "Return to start" (roundtrip default); designating a **Finish** should
   read **"Finish"** + that stop's arrival, finish pinned to the list end, and be
   revertable. (Switch logic exists in code; reported as misbehaving — needs
   verification.)

> The two earlier-reported symptoms — the panel being *cropped/not resizable* and
> its controls *aligning raggedly* — are consequences of issue 1 (the trip list
> forced into 256px). Feature A dissolves them; Feature C keeps only the residual
> row-alignment requirement.

## 3. Goals & Success Criteria

- **Aligned with intent:** toggling Trip View *switches* the desktop list region
  into the trip (full names + controls); toggling off restores the plain table
  unchanged. No redundant second list, no cramped side column.
- **Legible fidelity:** a user can tell what "Estimated" means and why their trip
  shows it (and what "Measured" requires) without leaving the panel.
- **Self-consistent math:** displayed total == sum of displayed per-leg times;
  arrivals reconcile with the existing `ItineraryTimeline` accumulation rule and
  the displayed figures. Verifiable by a unit/component test.
- **Discoverable:** every icon-only control reveals what it does on hover, at
  parity with its screen-reader label.
- **Honest per-movement modes:** each leg carries its own travel type; new legs
  start undefined (Any) with no auto-estimate; flight/Any-Air times are
  user-entered only — never an intercity hop timed as a walk.
- **Real multi-day schedule:** start is date+time, arrivals roll across days, and
  the **time limit** (a fixed goal) can be entered as a length *or* a finish-by
  deadline, with an "Over limit" indicator.

Stakes: moderate, multi-epic (incl. a schema migration). Success = the criteria
above passing review on the running desktop app plus green tests; mobile parity
follows in the mirror phase.

## 4. Features & Functional Requirements

FR IDs are global and stable, numbered in feature order.

### Feature A — Trip View switches the desktop list region into the trip (Issue 1, root)

- **FR-1** Toggling Trip View **on** makes the desktop filtered-results region
  **become** the trip stop list; the plain PoiTable is not shown at the same
  time. Toggling **off** restores the plain PoiTable and controls **unchanged**
  (no data loss).
- **FR-2** The trip stop list renders in the **wide list region** (not a 256px
  side column) as a trip-scoped table, columns left→right:
  1. **Reorder gutter** — drag handle **and** ▲▼ move buttons (mouse + keyboard;
     preserves NFR4).
  2. **Stop #** — order badge, with the Start/Finish glyph + ring.
  3. **Name** — **full POI name** (no unreadable truncation) with the **address
     sub-line** and enrichment-state icon, echoing PoiTable.
  4. **Dwell** — a **duration picker (HH:MM)** (FR-30), persisted as `DwellMinutes`.
  5. **Arrival** — relative offset always; wall-clock **and date** (locale-driven,
     FR-27) when a start time is set, shown together.
  6. **Start / Finish** — designate controls (○ / ⚑).
  7. **Actions** — **Focus on map** + **Open in Google Maps** only.

  The per-leg **travel time / distance / mode** is **not** a row column — it sits
  on the boundary between rows (FR-3). Dropped from the plain table in trip view:
  the Select checkbox, Coordinates, Collection chips, Added date, the per-row
  Move/Copy/Delete actions, **and the batch-action toolbar above the list**
  (`Select all` / `Move` / `Copy` / `Delete selected`) — all selection-based
  collection ops that no longer apply. The narrow `w-64` side column is removed.
  The trip list header carries only trip-relevant controls (stop count, TSP-Sort,
  Recompute, total travel time, start, time limit; Fit All / Labels stay on map).
- **FR-3** The **per-leg travel time is shown *between* the two stops it
  connects** — a compact connector on the shared edge of consecutive rows, **not**
  a row column and **not** a separate full row. The connector carries the leg's
  **mode control** (Feature F), **travel time** ("min" units, FR-16),
  **distance**, **fidelity badge**, and the **edit/reset** affordance (FR-25);
  an uncomputed/Any leg reads "—". The closing leg (roundtrip return, or the leg
  to a designated Finish) renders after the last row, ahead of the finish/return
  footer (Feature H).
- **FR-4** **Stop Order is the single canonical ordering for the collection.**
  Reordering in Trip View (drag, ▲▼, TSP-Sort, MCP) writes the shared
  `PoiCollectionItem.OrderIndex` (sole-writer `TripOrderingService.SetOrderAsync`),
  and the **plain Filtered Results list renders in that same order** when an order
  exists. One ordering entity; no divergent sorts; the order persists between the
  two views.
- **FR-5** The **map stays visible** beside/above the trip list (two-region work
  area preserved), and list↔map two-way selection sync keeps working.
- **FR-6** Desktop now matches the pattern mobile already uses
  (`MapPage.razor:160-165`): Trip View replaces the list content rather than
  adding a parallel list. *[ASSUMPTION] no drag-resizable splitter is needed once
  the list owns the wide region.*

### Feature B — Legible travel-time fidelity (Issue 2)

- **FR-7** Each fidelity badge (Estimated / Measured / Manual) explains its
  meaning in plain language on hover and to AT — replacing the circular
  "Provenance: Estimated." *[ASSUMPTION] e.g. "Estimated — straight-line
  approximation, not road distance."*
- **FR-8** When the deployment has no measured provider (the default `Mock`, so
  all legs are `Estimated`), the panel makes this legible: straight-line
  estimates, the expected default — distinct from the existing engine-unreachable
  fallback note, which keeps meaning only "we tried to measure and couldn't."
  *[ASSUMPTION] one quiet contextual line when all legs are non-fallback Estimated.*
- **FR-9** "Recompute travel times" must not imply it will upgrade fidelity when
  no measured provider is configured. *[ASSUMPTION] conveyed via FR-8's note/
  tooltip, not necessarily a control change.*
- **FR-10** The panel **recommends enabling OSRM** for measured road times: it
  explains the Estimated state **and points the user to how to enable** the
  optional self-hosted OSRM engine (e.g. a link to `docs/osrm.md`). This PRD still
  does **not** stand up or configure OSRM itself (Non-Goal §6) — it only guides;
  actually enabling it remains the operator's separate task.

### Feature C — Clean trip-row layout (Issue 1, residual)

- **FR-11** In the wide trip list, the row columns (FR-2) present as orderly
  aligned columns, not a ragged cluster — so the relocation doesn't re-create the
  cramped layout at a larger size.
- **FR-12** Row alignment holds across **stop-row** states: placeable vs.
  unplaceable, Start/Finish pinned, dwell set vs. empty, and long vs. short
  names. (Leg-level states — computing "—", manual override, reset — belong to
  the inter-row connector, FR-3, not the row.)

### Feature D — Reconciled travel-time arithmetic & units (Issue 3)

- **FR-13** The displayed trip **total** equals the sum of the displayed
  **per-leg** times — no drift from independent rounding.
- **FR-14** Displayed **arrivals** are produced by the **existing
  `ItineraryTimeline` accumulation rule** (the Start's dwell counts once at the
  beginning; each subsequent stop = prior arrival + leg travel + that stop's
  dwell) and **reconcile** with the displayed per-leg/total figures. This FR does
  **not** redefine the accumulation — it removes the rounding drift so legs,
  arrivals, and total agree.
- **FR-15** Rounding is applied once at the display edge from canonical seconds,
  consistently across legs/arrivals/total, preserving honesty qualifiers ("—"
  uncomputed, the Estimated/Measured/Manual provenance, the partial-trip em-dash).
  *[ASSUMPTION] round-then-sum from the displayed per-leg minutes; confirm it
  keeps partial-trip and fallback behaviors intact.*
- **FR-16** The **minute unit renders as "min"**, not "m" ("22 min", "1h 20 min",
  "<1 min") — today's "m" collides with distance "m" = meters ("397 m" beside
  "22m"). Hours stay "h"; distance meters stay "m". Changed in the
  `UiStrings.TripDuration*` strings; canonical seconds unchanged. (Shared layer —
  applies to both surfaces.)

### Feature E — Discoverable button tooltips (Issue 4)

- **FR-17** Every icon-only control in the trip list shows a hover tooltip naming
  its action: move up/down, Set/Unset Start (○), Set/Unset Finish (⚑), TSP-Sort,
  Recompute. Today only the drag handle has one.
- **FR-18** Tooltip text comes from `UiStrings` (reusing each control's existing
  `aria-label` where apt) and reflects the control's **state** ("Set as Start" vs.
  "Unset Start"; disabled edge/pinned controls read sensibly). Sighted + AT
  parity. *[ASSUMPTION] native `title`, matching the drag-handle pattern.*

### Feature F — Per-leg travel mode (Issue 5) — new capability

Each movement between two stops carries its own travel type, replacing the single
trip-wide mode. Largest item (schema + projection + UI); its own epic. Shared
layer — the data/VM changes reach both surfaces; mobile's per-leg control lands in
the mirror phase.

- **FR-19** Travel mode is a property of **each leg** (consecutive pair, plus the
  roundtrip closing leg), not the trip. Each leg shows + lets the user set its
  mode: Walk / Drive / Cycle / Any-Air.
- **FR-20** A **newly appeared** leg — from reorder, TSP-Sort, MCP, add/remove, or
  any recalculation that changes the consecutive pairs — defaults to **Any/Air
  ("undefined")**: no auto-computed time, reads "—" until the user acts.
  *"Undefined" and "Any/Air" are the **same state** — no auto time, manual-only;
  there is no separate "unset" value.* A deliberately-flown leg and a not-yet-
  decided leg are represented identically (a manual time is expected).
- **FR-21** A **ground mode** (Walk / Drive / Cycle) yields an automatic time
  (Estimated, or Measured under OSRM). **Any/Air legs are never auto-estimated** —
  time is user-specified only.
- **FR-22** A leg **unchanged** across a reorder (same From→To, same mode)
  **retains** its mode + cached time; only newly appeared pairs reset to Any/Air.
  (Directional mode-keyed `RouteSegment` cache already supports this — TRIP-CACHE-01.)
- **FR-23** The **trip-level mode selector is removed** — per-leg modes replace
  it; there is no global mode that drives legs. (A future "apply one mode to all
  legs" bulk action is a possible later nice-to-have, not in scope.) Per-leg mode
  persists per stop's outgoing leg (nullable `PoiCollectionItem.OutgoingTravelMode`);
  a small EF migration adds it, constrained by the `TravelMode.All` check pattern.
- **FR-24** The **per-leg travel mode is reachable from the `map_editor` MCP**, so
  the AI-assignment story (Epic 3) keeps working: `get_trip` reports each leg's
  mode, and a tool sets a leg's mode (alongside the existing `assign_stop_order` /
  `set_dwell_time`). Today `get_trip` reads a single trip-level mode and there is
  **no per-leg-mode tool** — retiring `PoiCollection.TravelMode` (FR-23) would
  otherwise break the MCP contract, leaving MCP-assigned legs stuck at Any with no
  way to set modes.
- **FR-25** The **per-leg travel time is user-editable** (inline on the connector
  or via a small popup) and **resettable to the auto value**. Editing sets a
  **Manual** override (Manual fidelity, never auto-overwritten — TRIP-MANUAL-01);
  **Reset** clears it and returns the leg to its auto time (Estimated/Measured for
  a ground mode; "—"/undefined for Any/Air). Generalizes today's Any/Air-only
  manual entry to any leg, plus an explicit reset.

### Feature G — Multi-day schedule: start time & time limit (Issues 6–7) — new capability

Trips span days; a time-of-day-only start and a raw-minutes "Time budget" are
insufficient. Shared layer; mobile pickers land in the mirror phase. *Note: the
field labelled "Time budget" is renamed **"Time limit"** and its overrun
indicator "Over budget" → **"Over limit"** (`UiStrings`).*

- **FR-26** **Start is specified as date AND time** (a date-time picker).
  Persisted in the existing `PoiCollection.TripStartTime` (`DateTime?` — **no
  schema change**); empty still means relative offsets only. The `type="time"`
  input that hard-pairs `DateTime.Today` is replaced.
- **FR-27** Wall-clock arrivals reflect the date and **roll across midnight /
  multiple days** — an arrival on a later day shows its date. Date/time are
  **locale-driven** (`CultureInfo.CurrentCulture`); no hard-coded order. Continuous
  accumulation is unchanged — overnight modeling stays out of scope (Non-Goals).
- **FR-28** The **time limit** (renamed from "Time budget") is a **fixed goal the
  user sets** — the most time the whole trip should take. It can be entered as a
  **duration** via a time picker (HH:MM), not only raw minutes. Persisted as
  `TimeBudgetMinutes` (**no schema change**); HH:MM ↔ minutes at the UI edge. The
  app compares the trip's total against it and shows the **"Over limit"**
  indicator when exceeded.
- **FR-29** The time limit can **alternatively** be entered by picking a
  **finish-by deadline** (date + time): the app computes the limit once as
  `deadline − start` (requires a start). This is an **input convenience only** —
  afterwards the limit is a **fixed goal stored as minutes** and does **not**
  recompute when the start or the trip changes (changing the trip is exactly when
  you want to see whether you still meet the goal, via "Over limit"). *This
  "finish-by deadline" is NOT the **Finish stop** of Feature H — that designates
  an end POI; this is a time goal. Distinct names, distinct concepts.*
- **FR-30** **Dwell is entered with a duration picker (HH:MM)**, not a raw-minutes
  box — consistent with FR-28. Persisted as canonical `DwellMinutes`; empty clears
  it. No schema change.

### Feature H — Finish designation & roundtrip readout (Issue 8)

- **FR-31** A trip is **roundtrip by default**; with no Finish, the end readout
  reads **"Return to start"** + the return-to-Start arrival.
- **FR-32** Pressing **Finish** on a stop makes the trip an **open path**: that
  stop becomes the Finish, is **pinned to the end of the list**, and the readout
  switches to **"Finish"** + its arrival time/date (date-aware, FR-27) — never
  "Return to start" while a Finish is set.
- **FR-33** The Finish designation is **revertable**: unsetting returns the trip
  to roundtrip and the readout to "Return to start," no data loss.
- *Note:* switch logic exists today (`IsRoundtrip => FinishPoiId is null`; Finish
  pins to order N; Set/Unset controls). Primarily **verify on the running app**
  (reported as misbehaving) and fix any gap.

## 5. Non-Functional Requirements & Constraints

- **Architecture & units.** Strict layering (markup-only `.razor` bridge →
  `TripViewModel` → services). Feature A is a markup/layout move in `MapPage.razor`
  reusing `TripStopList`/VM — no new ordering/timeline logic. Arithmetic
  (FR-13–15) lives in `ItineraryTimeline` / `TravelTimeFormatting` /
  `TripViewModel`, never the component. Canonical units unchanged
  (seconds/meters/minutes). No change to `RouteSegment` cache semantics or the
  default provider. Per-leg mode (Feature F) adds a nullable outgoing-leg mode
  column via a small EF migration, constrained by `TravelMode.All` (TRIP-SCHEMA-01),
  reusing the directional mode-keyed cache (TRIP-CACHE-01) — no new cache shape.
- **Cross-surface.** Shared-layer changes (FR-16 units; Feature D arithmetic;
  Feature F data/VM; Feature G persistence) are authored once and therefore apply
  to **both** desktop and mobile — mobile must remain correct (data/strings/times)
  even though its new-feature **controls** are deferred to the mirror phase. Don't
  break `MobileTripPanel` when changing shared code.
- **UI conventions.** All new/changed text via `UiStrings`. Tailwind `surface-*` /
  `on-surface-*` / `primary` tokens only. No group-B analyzer violations;
  `TreatWarningsAsErrors` holds.
- **Accessibility.** Preserve `aria-live` / `aria-label` parity; tooltips also
  available to AT; list↔map sync + keyboard reorder/select intact after relocation.
- **Testing.** Cover the desktop component path (bUnit) and the arithmetic
  invariant (unit). After any Trip VM/DI/schema change, run the Trip integration
  filter (`FullyQualifiedName~Integration&FullyQualifiedName~Trip`). Add a test
  asserting Trip-View-on hides the PoiTable and shows the wide stop list. Keep the
  existing mobile trip tests green.
- **No regressions** to map-side leg rendering, stop-order badges, selection sync,
  or per-collection toggle persistence.

## 6. Non-Goals

- **Mobile UI surfacing** of the new/changed features — deferred to the follow-up
  **mirror-to-mobile** phase. (Mobile already does the layout switch; shared-layer
  fixes reach it automatically — those are not deferred.)
- Standing up OSRM / changing the default `Mock` provider.
- Changing default **values** or adding auto-fill / overnight ("stop for the
  night") modeling. Input **affordances** do change (FR-26/28/29/30) — that is in
  scope; default values and auto-population are not.
- A drag-resizable map/list splitter (only relevant to the old side-panel model).
- Any further new trip features (export, scheduling automation, optimization
  beyond existing TSP).

## 7. Open Questions & Assumptions

- **[ASSUMPTION — FR-1/2]** Trip View on **hides** the PoiTable and renders the
  stop list in the same wide region; map layout otherwise unchanged.
- **[RESOLVED — FR-4]** No multi-collection case exists: Trip View is only
  available when **exactly one collection is in scope** (`TripViewModel`:
  `IsToggleAvailable` requires a single `ActiveCollectionId`). So the plain list
  sorts by that collection's Stop Order when an order exists; a collection never
  put into Trip View keeps its normal default sort. Nothing cross-collection to
  define.
- **[ASSUMPTION — FR-6]** No resizable splitter once the list owns the wide region.
- **[ASSUMPTION — FR-15]** Round-then-sum from displayed per-leg minutes; revisit
  if it conflicts with `ItineraryTimeline` honesty semantics.
- **[ASSUMPTION — FR-7]** Plain-language tooltip wording finalized in `UiStrings`.
- **[RESOLVED — FR-10]** The panel **recommends enabling OSRM** (explains
  Estimated + points to how, e.g. `docs/osrm.md`); it does not configure OSRM.
- **[RESOLVED — FR-20]** On the **first** toggle-on (initial seed), **all legs
  start Any/Air** (no times until modes are assigned).
- **[RESOLVED — FR-23]** The trip-level mode selector is **removed**; per-leg
  modes replace it (a "set all legs" bulk action is a later nice-to-have).
- **[RESOLVED — FR-24]** Feature F **extends the `map_editor` MCP** with per-leg
  mode read/write so AI-assigned trips can set modes.
- **[ASSUMPTION — FR-19/23]** Per-leg mode persists on the stop's outgoing leg;
  `PoiCollection.TravelMode` is retired from driving legs (kept only if FR-23
  picks the bulk-apply option).
- **[SEQUENCING]** A **mirror-to-mobile** phase follows desktop: surface per-leg
  mode, the inter-row connector, date/budget/dwell pickers, and tooltips in
  `MobileTripPanel`. Tracked here so the deferral is explicit, not forgotten.
