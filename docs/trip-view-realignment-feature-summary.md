# Feature Summary — Trip View: Layout Realignment & Honest Schedule

**Status:** Shipped 2026-06-15 · **Type:** Brownfield delta on the shipped Trip Planning slice
**Scope:** 4 epics · 19 stories · 33 FRs (FR-1…FR-33), 100% delivered
**Sources:** PRD `_bmad-output/archive/trip-view-realignment/planning-artifacts/prds/.../prd.md` ·
Architecture (RD1–RD13) `…/planning-artifacts/architecture.md` · epic retros
`…/implementation-artifacts/epic-{1,2,3,4}-retro-2026-06-15.md`. As-built reference:
[trip-planning.md](trip-planning.md), [architecture.md](architecture.md),
[data-models.md](data-models.md), [api-contracts.md](api-contracts.md).

> This is the **second wave** of Trip work. Wave 1 ("Trip Planning", see
> [trip-planning-feature-summary.md](trip-planning-feature-summary.md)) made a collection an
> ordered, mapped, timed loop. Wave 2 fixed how that trip is *presented* and made its schedule
> *honest* and *multi-day*.

---

## What shipped

The feature reworks the desktop Trip View from a cramped add-on into the primary work surface, makes
every displayed number internally consistent, gives each leg its own travel mode, and lets a trip
span real calendar days with a goal it can be held to. By feature group:

- **A/C — Desktop takeover & clean rows (E1):** toggling Trip View now *replaces* the filtered-results
  table with a full-width, column-aligned trip table (full POI names, address, enrichment icon,
  Focus + Open-in-Maps actions) — instead of bolting a 256px side column beside the unchanged
  PoiTable. Per-leg travel info moved onto a new inter-row **connector**. The map and list↔map
  selection sync are preserved.
- **B/D/E — Trustworthy, legible times (E2):** the displayed trip total now equals the sum of the
  displayed per-leg minutes (round-once display model), arrivals reconcile, the minute unit reads
  **"min"** (no longer colliding with distance "m"), fidelity badges explain themselves in plain
  language, a default-`Mock` deployment gets an "all estimates / enable OSRM" note, and every
  icon-only control has a hover tooltip. These are shared-layer fixes, so they reach mobile too.
- **F — Honest per-leg travel modes (E3):** travel mode is now a property of *each leg*
  (Walk/Drive/Cycle/Any-Air), not the whole trip. Ground legs auto-time; an Any/Air leg is never
  silently timed and reads "—" until the user acts. Reachable from the `map_editor` MCP. Carries the
  feature's one schema migration.
- **G/H — Multi-day schedule & honest finish (E4):** the start is a real date+time, arrivals roll
  across days showing their dates, the time limit can be an HH:MM duration or a finish-by deadline
  (with an "Over limit" warn), dwell uses an HH:MM picker, and a designated Finish reads "Finish" +
  its dated arrival instead of "Return to start" — revertably.

**Single canonical order:** Stop Order is now one ordering for the collection — the plain list
follows the same order the Trip list sets (FR-4).

---

## Key decisions & rationale (RD1–RD13)

- **One additive column is the whole schema cost (RD1).** Per-leg mode lives as a nullable
  `PoiCollectionItem.OutgoingTravelMode` where **`null` ≡ Any-Air** (one "undefined" state, no
  separate sentinel — `TRIP-LEGMODE-01`). The leg is owned by its **From** stop, keeping the
  directional `(From,To,Mode)` `RouteSegment` cache (`TRIP-CACHE-01`) unchanged.
- **Round once at the display edge (RD4, `TRIP-RECONCILE-01`).** The drift bug was display-only —
  legs were truncated independently while the total summed seconds. The fix rounds each leg once
  (`TravelTimeFormatting.DisplayMinutes`, the sole rounding edge) and derives both arrivals and the
  total from those same rounded minutes. Canonical seconds are untouched, so honesty qualifiers
  ("—", Estimated/Measured/Manual, partial-trip em-dash) survive.
- **TSP stays mode-invariant (RD3).** Ordering must happen *before* per-leg modes exist, so the cost
  matrix is built from straight-line/haversine distance, never per-leg mode — breaking the
  chicken-and-egg without changing the NN+2-opt algorithm.
- **Compute the finish-by limit once (RD10, `TRIP-SCHEDULE-01`).** A deadline is convenience input:
  it's converted to a fixed `TimeBudgetMinutes` once (`deadline − start`) and never stored or
  recomputed — so changing the start later doesn't silently move the goal.
- **Manual overrides are sacrosanct (RD7, `TRIP-MANUAL-01`).** A user-typed time is a `Manual`
  `RouteSegment` row, never auto-overwritten or invalidation-deleted; reset is the only (explicit)
  deleter, after which a ground leg recomputes and an Any/Air leg returns to "—".
- **MCP contract follows the data (RD6).** Retiring the trip-wide mode forced the MCP to match:
  `get_trip` reports each leg's `travelMode` (trip-level field removed) and a new verb-first
  `set_leg_travel_mode` tool sets a leg by its From-stop id.
- **Layout is markup reuse, not new logic (RD8).** The desktop takeover mirrors the switch mobile
  already performs — it reuses `TripStopList`/`TripViewModel` with no new ordering/timeline logic.
- **Keep, don't drop, the old column (RD1a).** `PoiCollection.TravelMode` was retired as the leg
  driver but **left as a dead column** (still written by the inert mobile selector) rather than
  dropped — the safe fallback, deferred to a future cleanup.

---

## Architecture deltas (vs. before this feature)

| Area | Before | After |
|------|--------|-------|
| Desktop Trip View | `TripStopList` in an additive 256px side column beside `PoiTable` | `TripStopList` **takes over** the wide results region; `PoiTable` hidden while on |
| Per-leg info | crammed into stop-row columns | new presentational `LegConnector` (↓ glyph · time · distance · fidelity · mode pill · manual edit/reset) on the row boundary |
| Travel mode | one trip-wide `PoiCollection.TravelMode` + a `TravelModeSelector` | per-leg `PoiCollectionItem.OutgoingTravelMode` + per-leg `LegModePill`; trip-wide selector removed (desktop) |
| Times display | per-leg truncation drifting from the total | round-once reconciled model; total == Σ legs; "min" unit |
| Schedule | `type="time"` start hard-paired to today; raw-minute budget | `datetime-local` start; date-aware multi-day arrivals; HH:MM/deadline time-limit; HH:MM dwell |
| Schema | — | one additive migration `AddOutgoingTravelMode` (nullable column + `CK_PoiCollectionItem_OutgoingTravelMode`), applied via startup `MigrateAsync` |
| MCP | trip-level `travelMode`; no per-leg control | per-leg `travelMode` in `get_trip`; new `set_leg_travel_mode` |
| Write-paths | `TripOrderingService` sole writer of `OrderIndex` | extended: also the sole writer of `OutgoingTravelMode` (reorder mode-reset + `SetOutgoingTravelModeAsync`) |

New invariants tagged in source for greppability: `TRIP-LEGMODE-01`, `TRIP-RECONCILE-01`,
`TRIP-SCHEDULE-01` (alongside inherited `TRIP-CACHE-01`/`TRIP-MANUAL-01`/`TRIP-SCHEMA-01`).
No infrastructure change — single Blazor Server container + SQLite, default `Mock` provider, OSRM
still an opt-in sidecar this feature only *recommends*.

---

## Lessons (from the epic retrospectives)

- **The per-story fresh-context adversarial review caught real defects the green unit suite missed —
  every epic.** Examples it surfaced and that were fixed before commit: a reorder shape-flip that
  left a resurrected closing leg's mode stale (E3), focusable controls trapped inside `aria-hidden`
  (E1), a persistent hint wrongly rendered as an `aria-live` region (E2), and a round-then-sum edge
  (E2). Standing practice: implement in a fresh subagent, independently re-run build+tests, then
  review in a *separate* fresh context.
- **Capture old state before the mutating caller overwrites it.** The E3 reorder bug came from
  reading the Finish pin once (already mutated); the fix threads the *prior* trip shape — and a bool
  "provided" flag was essential because `null` is a valid (roundtrip) shape, not "unsupplied".
- **Canonical units at the edge, every time.** All of Epic 4 changed only input affordances and
  display; the stored `DateTime?`/`int?` fields and the accumulation math were never touched. A
  "computed-once goal" stores the *result*, never the input it was derived from.
- **Shared-layer changes reach mobile by nature — prove it by running the mobile suite.** Run the
  Trip integration filter after any DI/VM-ctor/schema change; it's the load-bearing check the unit
  suite can't give.
- **Keep the three leg-projection sites mirrored** (VM `BuildLegs`, background compute
  `DirectionalPairs`, MCP `get_trip`) — they must agree on leg set, shape, and `(From,To,Mode)`
  keying.

---

## Known follow-ups (carried tech-debt)

- **A11 — per-leg Manual override row orphaned on mode change.** Setting a Manual time on a leg then
  changing that leg's mode leaves the old mode-keyed `Manual` `RouteSegment` row stranded. Harmless
  to display (projection keys by the current mode) but stale; fix = delete/migrate the old-mode
  Manual row in `SetOutgoingTravelModeAsync`.
- **Mirror-to-mobile (deferred phase).** `MobileTripPanel` still shows the previous controls
  (number dwell/budget, `type="time"` start, the now-inert trip-wide `TravelModeSelector`) and lacks
  the per-leg pill, connector edit, and the new schedule pickers. The shared logic/data/strings
  already reach mobile correctly — only the controls are deferred.
- **`PoiCollection.TravelMode` dead column** — kept as the RD1a fallback; a true drop is optional
  cleanup once the mobile selector is removed.

---

## Verification at close

Full suite **1063/1063 green** at feature close; build clean under `TreatWarningsAsErrors`. Each
story shipped with bUnit/unit coverage and passed the Trip integration filter; the reconciliation
invariant and the takeover (Trip-View-on hides `PoiTable`) are covered by dedicated tests.
