# Feature Summary — Bulk Travel Mode Assignment

**Status:** Shipped 2026-06-20 (`523043e`) · **Type:** Quick-dev brownfield delta on the shipped Trip View slice
**Feature code:** TRIP-BULKMODE-01 · **Scope:** Desktop-only · 1 spec (no epics/stories — lightweight PRD + spec cycle)
**Sources:** PRD `_bmad-output/planning-artifacts/prds/prd-maps_editor-2026-06-20/prd.md` (+ `addendum.md`,
`.decision-log.md`, `review-rubric.md`) · Spec `_bmad-output/implementation-artifacts/spec-bulk-travel-mode.md`
(incl. its Spec Change Log) · requirements draft `_bmad-output/bulk-travel-mode/planning-artifacts/requirements.md`.
As-built reference: [trip-planning.md](trip-planning.md), [component-inventory.md](component-inventory.md),
[data-models.md](data-models.md). Baseline commit: `a6114bd`; shipped in `523043e`.

> A small, sharp unblock on top of the Trip View work
> ([trip-view-realignment-feature-summary.md](trip-view-realignment-feature-summary.md),
> [trip-stops-panel-compaction-feature-summary.md](trip-stops-panel-compaction-feature-summary.md)).
> Where those milestones made the schedule honest and the panel compact, this one removes a tedious
> N-clicks-before-anything-works chore. Pure desktop behavior — no schema, migration, endpoint, MCP,
> or provider change.

---

## What shipped

A single header control in the Trip stops panel that assigns one travel mode to **every** leg of the
active trip at once, with an explicit opt-in for overwriting legs that already carry a mode.

The problem it solves: a trip leg defaults to **Any/Air**, which the background service never
auto-computes. An uncomputed leg has null `Fidelity`, which keeps `IsAnyLegComputing` true, which keeps
the **Sort** (Traveling-Salesman) and **Recompute** buttons disabled. Until now the only way out was to
set a ground mode on each leg individually — N selections on an N-stop trip before anything useful
happened. The new control turns that into **one** deliberate action.

- **One bulk control in the header action row.** A new `BulkLegModeSelector.razor` sits beside Sort /
  Recompute (inside the `OrderedLegs.Count > 0` block), so it appears under the same condition those
  actions do. It offers exactly the existing four modes — **Drive / Walk / Cycle / Any/Air** — presented
  like the per-leg `LegModePill` (glyphs, popover, `UiStrings` copy), opening in an unselected
  placeholder state so picking a mode is always deliberate.

- **An overwrite toggle, default off (non-destructive).** With overwrite **off**, the chosen mode fills
  **only** legs currently in Any/Air; legs with an explicit mode are left alone. With overwrite **on**,
  every leg is reassigned, replacing existing per-leg modes. A planner's hand-tuned modes are never lost
  by default.

- **"All legs" matches how the trip itself is built.** The from-stop set mirrors
  `BuildLegs` / `DirectionalPairs`: every consecutive from-stop `0..N-2`, plus — on a roundtrip with no
  distinct Finish — the closing leg (last stop's outgoing mode). An open path's final stop is not a
  from-stop and is left untouched.

- **Picking Any/Air in bulk is a valid revert, not an error.** All legs return to Any/Air, times read
  "—", and Sort / Recompute disable again. Existing ground-mode route-cache rows aren't recomputed or
  discarded — they simply stop being referenced.

- **One write, one refresh, one recompute.** The assignment persists as a single gated transaction
  through the existing single writer, then does one projection refresh and one `Notify()`; a ground mode
  signals the background travel-time trigger exactly as a single per-leg change does. Once legs settle,
  Sort / Recompute enable automatically.

---

## Key decisions & rationale

- **The control is NOT gated on `IsAnyLegComputing` (FR-13 reversed during implementation).** The headline
  decision. The original plan disabled the control while any leg was computing. But an Any/Air leg has
  null `Fidelity`, so `IsAnyLegComputing` is true *precisely on the all-Any/Air trips the control exists
  to fix* — gating on it would disable the control exactly when it is needed, and overwrite-off (which
  only touches Any/Air legs) could never fire. Reversed: the control is always enabled when legs are
  present, and disabled only **transiently while its own bulk request is in flight** (anti-double-submit).

- **Overwrite defaults off.** The counter-metric was explicit: a planner who hand-tuned individual leg
  modes must not lose that work by default. Overwriting is destructive only behind a deliberate opt-in.

- **One gated transaction batch writer — not a per-leg loop.** Rather than calling the per-leg
  `SetOutgoingTravelModeAsync` once per stop, a new batch method writes all affected from-stops in a
  single `SaveChanges` under the SQLite write lock (NFR-5: no per-leg DB round-trip; NFR-2: one refresh,
  one notify, at most one flip of the buttons' enabled state).

- **From-stop set mirrors `BuildLegs` / `DirectionalPairs`.** "All legs" is defined by the same leg
  composition the trip already uses, so the bulk write and the trip's own leg model can never diverge
  (roundtrip closing leg included; open-path final stop excluded).

- **No new write path; the component stays presentational.** All persistence flows through
  `ITripOrderingService`, the sole writer of `OutgoingTravelMode`; the Razor control raises exactly one
  VM command and never touches services or the DB (NFR-1). No new modes/providers; the legacy
  collection-wide `PoiCollection.TravelMode` path is untouched.

- **Overwrite-on can blank a Manual time — accepted, no confirm (for now).** Switching a leg's mode
  changes its `(From, To, Mode)` cache key, so a leg with a Manual time under its old mode reads "—"
  under the new one until recomputed. This is a consequence of an explicit user opt-in (distinct from
  the background-recompute protection, which still holds). A confirm prompt is deferred (open item A6).

---

## Architecture deltas (vs. before this feature)

| Area | Before | After |
|------|--------|-------|
| Bulk mode assignment | none — set mode one leg at a time (N actions) | one header control assigns a mode to all legs at once (1 action) |
| Ordering service | `SetOutgoingTravelModeAsync` (single from-stop) | + `SetAllOutgoingTravelModesAsync(collectionId, mode, overwriteExisting, ct)` — batch writer, single gated transaction, from-stop set mirrors `DirectionalPairs`, validates mode |
| View model | `SetLegModeAsync` (single leg) | + `SetAllLegsModeAsync(mode, overwriteExisting)` — same guard → service → refresh → trigger (ground only) → one `Notify()` shape |
| New component | — | `Components/Shared/Trip/BulkLegModeSelector.razor` (mode menu + overwrite checkbox; mirrors `LegModePill` conventions) |
| Header action row | Sort / Recompute only | + `<BulkLegModeSelector Vm="Vm" />` mounted beside them (legs-present block) |
| Copy | per-leg `TripTravelMode*` strings | + bulk selector label/aria + overwrite-checkbox label/aria in `UiStrings.cs` (reusing existing mode names) |
| Availability gate | (proposed) disabled while `IsAnyLegComputing` | NOT gated on compute state; disabled only while its own request is in flight |

**No schema, migration, endpoint, or MCP change.** A bulk assignment changes only `OutgoingTravelMode` —
never Stop Order, Start/Finish, or the time budget. Single Blazor Server container + SQLite, default
`Mock` provider, OSRM still an opt-in sidecar. `MobileTripPanel.razor` was explicitly **not** mirrored.

---

## Lessons

- **Watch for the self-defeating constraint.** The standout catch of this cycle: a guard that reads
  sensible in the abstract ("don't let the user act while things are computing") was, on inspection,
  guaranteed to be true exactly in the scenario the feature targets — making the control permanently
  disabled when needed. Caught during step-03 implementation and folded back into the PRD and spec as a
  frozen amendment (FR-13). Lesson: when a control's enable condition is the *inverse* of the state it
  exists to repair, gating on that state is a logic trap, not a safety rail.

- **A batch is one transaction, not a loop with the same body.** NFR-2/NFR-5 wanted one write, one
  refresh, one notification, one button flip. Reusing the per-leg writer in a loop would have satisfied
  the behavior but violated the non-functionals (N round-trips, N refreshes). The dedicated batch method
  on the service was the right seam.

- **"All legs" must be defined once.** Anchoring the from-stop set to `BuildLegs` / `DirectionalPairs`
  (rather than re-deriving it) is what keeps the roundtrip closing leg and the open-path final-stop
  exclusion correct without a second source of truth.

---

## Known follow-ups (deferred)

- **A2 — mobile mirror still deferred.** `MobileTripPanel.razor` does not get the bulk control; the
  desktop/mobile divergence is intentional for now (consistent with the prior mirror-to-mobile defer).
- **A6 — confirm-on-overwrite still open.** Overwrite-on can clear a Manual time under the old mode key.
  Default for now is **no confirm** (it is already an explicit opt-in); a prompt would only be added if
  product/UX reopens A6.
- **A3 — Any/Air bulk hint/toast** (the optional "times will show —" note) deferred as polish.

---

## Verification at close

Shipped `done`, merged to `master` (`523043e`, clean tree). Stated verification:
`dotnet build LucidCartographer/LucidCartographer.csproj` clean, and
`dotnet test LucidCartographer.Tests --filter "FullyQualifiedName~Trip"` green — **full suite
1121/1121**. New coverage: `TripViewModelBulkModeTests` walks the I/O matrix (fill-empty,
preserve-manual, overwrite-on, Any/Air revert, roundtrip closing leg, open path, the no-mutation
invariants for Stop Order / Start-Finish / budget, and invalid-mode throws), and
`BulkLegModeSelectorTests` asserts visibility (legs > 0), **enabled even when `IsAnyLegComputing` is
true** (the all-Any/Air case the feature exists for), and that selecting persists the mode with
overwrite both off and on.
