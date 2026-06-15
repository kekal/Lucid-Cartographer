---
title: "Trip View Realignment — Design Delta"
status: final
created: 2026-06-15
updated: 2026-06-15
inherits: "ux-maps_editor-2026-06-11/DESIGN.md"
components:
  trip-stop-row: "Wide trip-table row; echoes {components.table-row} rhythm, trip-scoped columns"
  leg-connector: "Inter-row leg strip on the shared edge of two stop rows"
  leg-mode-pill: "Per-leg travel-mode pill that opens a mode menu"
  schedule-picker: "Native datetime-local / time inputs, token-styled"
---

# Trip View Realignment — Design Delta

> A **delta** spine inheriting the canonical `DESIGN.md`
> (`ux-maps_editor-2026-06-11`). **Tokens — colors, typography, spacing, radius,
> elevation — are unchanged.** This delta adds visual specs only for the new Trip
> View components. Behavioral rules live in this run's `EXPERIENCE.md`.

## Components

### Trip stop row (`components.trip-stop-row`)
Replaces the old cramped `Stop list` row. Echoes `{components.table-row}` (~44px,
virtualized rhythm) but trip-scoped and full-width. Columns L→R: reorder gutter
(grip + ▲▼, `{colors.on-surface-variant}`) · stop# badge (`{colors.primary}` fill,
`{colors.on-primary}` numeral; Start/Finish ring + glyph) · name
(`{colors.on-surface}`) over address sub-line (`{colors.on-surface-variant}`,
`text-xs`) + enrichment icon · dwell input · arrival (two lines: offset
`{colors.on-surface}`, wall-clock+date `{colors.on-surface-variant}` `text-[10px]`)
· Start/Finish icon buttons · action icons (focus, open-in-maps). Selected row:
`{colors.primary}/10` tint + inset `{colors.primary}` ring.

### Inter-row leg connector (`components.leg-connector`)
A thin strip sitting **on the shared border between two stop rows**, inset to
align under the name column. Background `{surface-container}` (a hair distinct
from the row), separated by hairline dividers. Single line, `text-xs`:
`↓` glyph (`{colors.on-surface-variant}`) · **mode pill** · travel time
(`{colors.on-surface}`, "min" units) · `·` distance (`{colors.on-surface-variant}`)
· fidelity badge. The **reset (↺)** button is hidden at rest and appears on
hover/focus of the connector (`{colors.on-surface-variant}`, hover
`{surface-container-high}`). The closing leg renders as the same connector after
the last row.

### Per-leg mode pill (`components.leg-mode-pill`)
Rounded-full pill, `text-xs`. **Set:** `{colors.primary}/10` fill,
`{colors.primary}` text, mode glyph (walk/drive/cycle/flight) + label. **Undefined
(Any):** outline only (`{colors.on-surface-variant}` border + text), label
"Any — set mode" with a `?`/help glyph. Click opens a small menu (Material list)
of the four modes; the active one checked. Manual-override legs keep their mode
pill and badge **Manual** (`{colors.primary}` outline) per the inherited fidelity
badge spec.

### Schedule pickers (`components.schedule-picker`)
**Native** inputs, token-styled (no bespoke calendar): Start = `datetime-local`;
Time limit = `time` (HH:MM duration) with an alternate `datetime-local` for the
finish-by deadline; dwell = `time` (HH:MM). All use the inherited input chrome
(`{surface-container-low}` fill, `{colors.on-surface}` text, hairline border,
focus ring `{colors.primary}`). "Over limit" chip: amber soft-warn
(`text-amber-600` on `{surface-container}`) — never `{colors.tertiary}`/red.

## Do's and Don'ts
- **Do** keep the leg connector to one line at rest; reveal `↺` only on hover/focus.
- **Do** reuse `{components.table-row}` density + the existing badge/chip tokens —
  no new color ramp.
- **Don't** widen the row to re-create the old cramped feel; the takeover exists to
  give names room.
- **Don't** color an undefined/Any leg as if it were an error — it is a neutral
  outline pill, not `{colors.tertiary}`.
- **Don't** style the "Over limit" warn state red; it is amber soft-warn.
