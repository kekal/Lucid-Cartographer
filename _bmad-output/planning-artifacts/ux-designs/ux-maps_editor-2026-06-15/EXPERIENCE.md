---
title: "Trip View Realignment — Experience Delta"
status: final
created: 2026-06-15
updated: 2026-06-15
inherits: "ux-maps_editor-2026-06-11/EXPERIENCE.md"
---

# Trip View Realignment — Experience Delta

> A **delta** spine. It inherits the canonical `EXPERIENCE.md`
> (`ux-maps_editor-2026-06-11`) and overrides only the Trip View patterns the
> realignment PRD (`prds/prd-maps_editor-2026-06-15`) changes. Visual specs live
> in this run's `DESIGN.md`; tokens are referenced by `{name}` from the canonical
> design system (unchanged). **This delta wins on conflict** with the inherited
> spine for Trip View; everything not mentioned here is unchanged.

## Foundation

- **Surface:** desktop Map page Trip View. **Desktop is built now**; the
  **mobile** panel mirrors these patterns in a follow-up phase (shared logic/data
  reaches mobile already — only its controls are deferred).
- **Scope rule:** Trip View is only available when **exactly one collection** is
  in scope (≥2 placeable POIs). No multi-collection trip.

## Component Patterns (delta)

| Component | Behavioral rules |
|---|---|
| **Trip View takeover** (replaces the additive side column) | Toggling Trip View **on** makes the desktop filtered-results region *become* the trip stop list; the plain `POI table` is hidden. Toggling **off** restores it unchanged (no data loss). The map stays; list↔map two-way sync is preserved. *Conforms the build to the original toggle intent.* |
| **Trip stop table** (replaces the cramped `Stop list` row) | Wide, trip-scoped table. Columns L→R: reorder gutter (drag handle **+** ▲▼) · stop# badge (Start/Finish glyph) · **name + address + enrichment icon** · **dwell** (HH:MM) · **arrival** (offset; + wall-clock & date when a start is set) · Start/Finish (○/⚑) · actions (**Focus on map**, **Open in Google Maps**). Row click selects the stop (list→map); dwell/action clicks `stopPropagation`. **Leg time is NOT in the row** — it lives on the connector. **Dropped in trip view:** select checkbox, coordinates, collection chips, added date, per-row move/copy/delete, and the batch toolbar (selection-based collection ops). |
| **Inter-row leg connector** *(NEW)* | A compact connector on the **shared edge between two consecutive stop rows** (and a closing connector after the last row, before the finish/return footer). **Single line:** mode pill · travel time (`{min}` units) · distance · fidelity. Carries the leg's **mode control** and **edit/reset** affordance. An undefined/Any or uncomputed leg reads **"—"**. *[ASSUMPTION] left-indented under the name column (per the carried-in mockup); confirm at mock review.* |
| **Per-leg mode control** *(replaces per-trip selector)* | A **pill** on each connector showing the leg's mode ("Drive") or **"Any — set mode"** when undefined. Click opens a small menu: **Walk / Drive / Cycle / Any-Air**. A **ground** mode (Walk/Drive/Cycle) yields an automatic time; **Any/Air** is **manual-only** (never auto-estimated). There is **no trip-wide mode selector**. |
| **Leg-time edit / reset** | Click the connector's time → it becomes an **inline editable field**; entering a value sets a **Manual** override (Manual fidelity, never auto-overwritten). A **reset (↺)** — shown on **hover/focus only** — clears the override back to the auto value (Estimated/Measured for a ground mode; "—" for Any/Air). Generalizes manual entry to **any** leg. |
| **Fidelity badge** | Now **self-explaining**: hover/AT tooltip in plain language ("Estimated — straight-line approximation, not road distance"). When all legs are default-`Mock` Estimated, a **quiet contextual note** explains the state and **recommends enabling OSRM** (links to `docs/osrm.md`) — distinct from the engine-unreachable fallback note. (Badge/line visuals unchanged.) |
| **Recompute** | Unchanged, except its copy must not imply a fidelity upgrade when no measured provider is configured. |
| **Start / Finish + readout** | Roundtrip is the default → footer reads **"Return to start"** + return arrival. Pressing **Finish** on a stop pins it to the **end of the list** and flips the footer to **"Finish"** + that stop's arrival (date-aware) — never "Return to start" while a Finish is set. **Revertable** (unset → roundtrip). |
| **Schedule controls** (header) | **Start** = native **date+time** picker (empty ⇒ relative offsets only). **Time limit** (renamed from "Time budget") = native **HH:MM duration** OR a **finish-by deadline** (date+time → limit once = `deadline − start`); a fixed goal, never recomputed. Overrun shows **"Over limit"** (amber soft-warn). |
| **Ordering actions (incl. MCP)** | Unchanged paths (drag / ▲▼ / TSP-Sort / MCP). MCP now also **reads/sets per-leg mode** so AI-assigned trips can choose modes. |

## State Patterns (delta)

| State | Treatment |
|---|---|
| **Undefined / Any leg** *(NEW)* | A newly-appeared leg (after reorder / TSP / add-remove / recalc) shows **"—"** with the **"Any — set mode"** pill — it **awaits a mode**, distinct from *computing*. No auto time until a ground mode is chosen or a manual time entered. A leg unchanged across a reorder keeps its mode + time. |
| **Manual override / reset** | A leg with a typed time shows the **Manual** badge; the **↺ reset** (hover/focus) returns it to auto. |
| **Multi-day rollover** *(NEW)* | An arrival on a later calendar day than the start shows its **date** alongside the time (locale-driven), so a multi-day trip reads on its real days. |
| **Over limit** *(renamed)* | Amber soft-warn when the trip total exceeds the set Time limit; informational, non-blocking; absent when no limit is set. (Was "Time-budget overrun".) |
| **Reconciled timeline** | Displayed total = sum of displayed per-leg times; arrivals follow the existing accumulation rule and reconcile with the shown figures (no rounding drift). Honesty qualifiers ("—", provenance, mixed-fidelity `~`) unchanged. |

*Unchanged states:* leg-computing, unplaceable stop/timeline, mixed-fidelity total, routing-provider-down.

## Interaction Primitives (delta)

- **Set a leg's mode on its connector** (pill → menu). *(NEW)*
- **Edit a leg's time inline on its connector; reset (↺) to auto.** *(NEW)*
- Reorder = drag **or** ▲▼ (keyboard); list↔map sync; reversible toggles — **unchanged**.
- **Still banned:** auto-reordering, silently dropping unplaceable POIs, showing an unmeasured time as if measured, **auto-estimating an Any/Air leg**, sending coordinates to a third party without surfacing it.

## Voice and Tone (new microcopy)

- Fidelity tooltip: *"Estimated — straight-line approximation, not road distance."* / *"Measured — real road route."* / *"Manual — you entered this time."*
- Mock-default note: *"All times are straight-line estimates. Enable OSRM for measured road times."* (+ link). *[ASSUMPTION] exact wording in `UiStrings`.*
- Undefined leg pill: **"Any — set mode"**. Overrun chip: **"Over limit"**. Field labels: **"Time limit"**, **"Start"**, **"Dwell"**.

## Accessibility Floor (delta)

- Every icon-only control gets a **`title`** at parity with its existing `aria-label` (move/start/finish/TSP/Recompute) — sighted + AT parity.
- Native pickers (`datetime-local`, `time`) keep keyboard + screen-reader support for free; reset (↺) is a real focusable button with an `aria-label`.
- Keyboard reorder (▲▼) and list↔map sync remain intact after the layout takeover.

## Key Flow — Yurik plans a 4-day Wrocław run

1. Yurik opens the **Plan C — Wrocław by train** collection, toggles **Trip View** → the POI list **becomes** the wide trip table; names are fully readable.
2. He drags two stops to reorder; the **new legs between them show "—" with "Any — set mode"** — nothing is silently timed as walking.
3. On the connector between the station and the old town he taps the pill → **Drive**; a measured-or-estimated time appears. Between the close clustered stops he picks **Walk**.
4. The intercity hop he sets to **Any-Air** and types the train time inline; it badges **Manual**.
5. He sets **Start** to *Thu 4 Jun, 09:00* (date+time) and a **Time limit** by picking a **finish-by deadline** *Sun 7 Jun, 18:00*.
6. **Climax:** the footer reads **"Finish"** at his designated last stop with a **dated** arrival — *Sun 7 Jun, 16:40* — comfortably under the limit (no "Over limit" chip). The schedule finally reads in real days, with honest per-leg modes.
