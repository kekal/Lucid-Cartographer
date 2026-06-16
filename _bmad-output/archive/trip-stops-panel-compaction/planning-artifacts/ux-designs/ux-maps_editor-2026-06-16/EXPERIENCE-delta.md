---
title: Trip stops panel — compaction & duration pickers (UX delta)
status: final
updated: 2026-06-16
surface: Desktop Trip View stop-list panel
target: LucidCartographer/Components/Shared/Trip/TripStopList.razor (+ LegConnector.razor)
sources:
  - .decision-log.md
---

# Trip stops panel — UX delta

A focused behavioral spec for four desktop-only changes. This is a delta on a
mature surface, not a full spine. **This spec wins on conflict with the mock.**
Mobile (`MobileTripPanel.razor`) is out of scope (mirror deferred).

## Foundation
Blazor/Razor, Tailwind Material tokens (`bg-surface-container-low`, `text-on-surface`,
`text-on-surface-variant`, `text-primary`, `border-surface-container-high`). Keep all
existing `UiStrings`, `aria-label`s, and `role=status`/`aria-live` regions intact —
this is a layout + control-type change, not a content rewrite. No new copy is needed.

## Change 1 — Header collapses to two slim rows
Today the header is five stacked, individually-bordered rows (current lines ~13–175).
Collapse the first three into **one row**, keep the schedule inputs as **a second row**,
and leave the OSRM note as its own conditional row.

**Row 1 (single line, one bottom border).** Replace the separate title row (L13–24)
and the total-travel-time row (L33–41) with one flex row:
- Left, inline, NOT justify-between: `{TripStopList}` · `{N}` stops · `{total travel time}`.
  Keep the existing `role=status`/`aria-live` count region and the total's `aria-label`
  (`TripTotalTravelTimeAria`); just relocate them onto this line. Use a middot/`·` or
  small gap as separators. The total still shows the em-dash for a partial trip.
- Right: the Sort + Recompute **buttons** (see Change 2).
- The total + buttons only render when `Vm.OrderedLegs.Count > 0` (same guard as today);
  when there are no legs, Row 1 shows just title + count.

**Row 2 (schedule).** Keep the Start / Limit / Finish block (current L79–132) as a
single wrapping row directly under Row 1. Start (datetime-local), the over-limit amber
chip, and the finish-by-needs-start hint are unchanged. **`Limit` and `Finish by` are
now a single linked value** — see Change 5.

**OSRM / estimate note.** Unchanged (current L145–175). It is already conditional
(renders only on Mock/fallback provider) — leave that logic exactly as-is so it
disappears once a measured engine (OSRM) is active. Do not collapse it to an icon.

Net: ~5 rows → 2 rows + 1 conditional note. Reclaims roughly three rows of height.

## Change 2 — Sort & Recompute become buttons
Current L46–66 renders them as `text-primary hover:underline` links. Make them
outlined icon buttons sitting at the right of Row 1:
- Keep `UiStrings.TripSortTspLabel` / `TripRecomputeLabel` text and the
  `TripSortTspAria` / `TripRecomputeAria` aria-labels + titles.
- Optional leading icons: `route` (Sort) and `refresh` (Recompute) via
  `material-symbols-outlined` (already used in this file).
- Preserve `disabled="@Vm.IsAnyLegComputing"` and the click handlers
  (`SortTravelingSalesmanAsync`, `RecomputeTravelTimesAsync`).
- Style as compact secondary buttons (bordered, `text-xs`), not filled primary —
  they are utilities, not the panel's primary action.
- Keep the `LastSortAnnouncement` sr-only live region.

## Change 3 — Stats no longer split left/right
Folded into Change 1: count and total now sit together on the left of Row 1 instead
of each being pushed to the far right of its own `justify-between` row.

## Change 4 — Duration inputs → uncapped HH:MM with ▲▼ steppers
Applies to **both** the per-POI dwell input and the per-leg movement-time input.

Shared control behavior:
- Masked **HH:MM** text entry (no AM/PM — already the case for dwell).
- **Hours uncapped**: accept 1–3+ hour digits, e.g. `125:30`. Replace the current
  dwell pattern `([01]?[0-9]|2[0-3]):[0-5][0-9]` with one that allows `\d{1,3}:[0-5][0-9]`
  (minutes still 00–59). Validate/normalize on change.
- **▲▼ steppers** adjacent to the field: each click = **±5 minutes**;
  **Shift+click = ±1 hour**. Clamp at a floor of `0` (`00:00`); no upper clamp beyond
  storage limits.
- Convert HH:MM ↔ canonical units only at this UI edge; the VM keeps its canonical
  value (dwell minutes; leg seconds). Do not change VM types.

Per-input specifics:
- **Dwell** (current L360–367, placeable; L246–251, unplaceable): already HH:MM text;
  add steppers + lift the hour cap. Keep `SetDwellMinutesAsync`.
- **Per-leg movement** (`LegConnector.razor`, current L24–48): today a
  `type=number` **minutes** field (click-to-edit). Switch to the same HH:MM + stepper
  control. Keep `SetManualLegTimeAsync` (convert HH:MM → minutes at the edge), the
  click-to-edit affordance, and the Manual-leg reset (↺) button.

A11y: steppers are real `<button>`s with aria-labels (e.g. "increase dwell by 5
minutes"); the text field keeps its existing aria-label. Steppers are keyboard
reachable; ArrowUp/ArrowDown on the focused field should mirror the +5/−5 behavior.

## Change 5 — Limit ⇄ Finish-by are one linked value; Limit is a duration picker
Today `Limit` (HH:MM, current L95–103) and `Finish by` (datetime-local, current
L109–116) both write to the same canonical `TimeBudgetMinutes`, but they're shown as
unrelated inputs and `Limit` is capped ≤24h because the deadline path was made to own
the >24h horizon. Unify them:

- **`Limit` uses the same uncapped HH:MM + ▲▼ stepper control as Change 4** (dwell/leg):
  drop the `([01]?[0-9]|2[0-3]):[0-5][0-9]` cap → `\d{1,3}:[0-5][0-9]`; ±5min / Shift±1h;
  floor 0. An uncapped duration now covers the >24h case, so `Limit` no longer renders
  empty for large budgets.
- **One canonical value = the duration** (`TimeBudgetMinutes`). `Finish by` is a derived
  view = `start + Limit`.
- **Bidirectional, edit-time mirroring:**
  - Editing `Limit` (`OnDurationChangedAsync`) updates the displayed `Finish by`
    (= start + limit) when a start exists.
  - Editing `Finish by` (`OnFinishByChangedAsync`) back-computes `Limit` =
    `finish − start` and persists it via the same `SetTimeBudgetMinutesAsync` path.
  - Both write the same canonical minutes — never two divergent stores.
- **Start changes:** `Finish by` re-derives (= start + limit); the duration is
  unchanged. (Supersedes the current one-shot deadline→minutes conversion that
  deliberately did NOT recompute — see the comment at current L104–108.)
- `Finish by` still **requires a start** (disabled + `TripFinishByNeedsStartHint` when
  absent); `Limit` is editable with no start (pure duration).
- Keep the over-limit amber chip (`Vm.Timeline.IsOverBudget`) and all existing
  `UiStrings`/aria-labels for both inputs.

A11y: `Finish by` mirroring `Limit` should not steal focus mid-edit; announce nothing
new (the existing regions suffice).

## Acceptance checks
1. Header occupies two rows (+ the conditional OSRM note) — visibly shorter; no
   stat is pushed to the far right of an otherwise empty row.
2. Sort & Recompute are bordered buttons, still disabled while legs compute, same
   aria-labels and actions.
3. Dwell and per-leg fields accept `>24h` (e.g. `30:00`); ▲▼ = ±5 min, Shift = ±1h,
   floor at 0; values round-trip through the VM unchanged in canonical units.
4. `Limit` is an uncapped HH:MM duration picker; setting `Limit` updates the shown
   `Finish by` (= start + limit) and vice-versa, both via `TimeBudgetMinutes`. A
   `Limit` > 24h (e.g. `30:00`) is accepted and not blanked. Changing `Start` shifts
   `Finish by` while the duration holds. `Finish by` stays disabled with no `Start`.
5. OSRM note still appears on the Mock/fallback provider and disappears under a
   measured engine — behavior untouched.
6. Existing Trip integration tests pass (run the Trip filter after VM/markup edits,
   per project rule). Note: tests asserting the old one-shot deadline behavior or the
   ≤24h limit cap will need updating to the linked model.

## Out of scope
Mobile panel; any change to leg distance, fidelity badge, travel-mode pill, drag/
reorder, or selection behavior.
