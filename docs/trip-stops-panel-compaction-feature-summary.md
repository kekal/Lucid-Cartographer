# Feature Summary — Trip Stops Panel: Header Compaction & Unified Duration Pickers

**Status:** Shipped 2026-06-16 · **Type:** Quick-dev brownfield delta on the shipped Trip View slice
**Scope:** Desktop-only · 2 specs (a duration-input bugfix + the compaction feature) · no epics/stories (lightweight spec-driven cycle)
**Sources:** Spec `_bmad-output/archive/trip-stops-panel-compaction/implementation-artifacts/spec-trip-stops-panel-compaction.md` ·
predecessor spec `…/spec-trip-duration-hhmm-text-input.md` · UX delta
`…/planning-artifacts/ux-designs/ux-maps_editor-2026-06-16/EXPERIENCE-delta.md`. As-built reference:
[trip-planning.md](trip-planning.md), [component-inventory.md](component-inventory.md),
[data-models.md](data-models.md). Baseline commits: `9cea948` (HH:MM text), `314d14a` (compaction).

> This is a **polish milestone** on top of Wave 2
> ([trip-view-realignment-feature-summary.md](trip-view-realignment-feature-summary.md)). Where Wave 2
> made the trip schedule *honest* and *multi-day*, this milestone makes the stop-list panel *compact*
> and its duration controls *consistent*. Pure desktop UI/formatting — no schema, endpoint, MCP, or
> infrastructure change.

---

## What shipped

Two related desktop changes, shipped back-to-back, that tighten the Trip stop-list panel and unify how
every "duration" value is entered:

- **Duration fields stopped pretending to be clocks (bugfix, `9cea948`).** The Time-limit and per-stop
  Dwell fields rendered with `<input type="time">`, which in a 12-hour locale draws an AM/PM clock
  selector. These are **elapsed durations** (HH:MM), not a time of day, so AM/PM is meaningless. They
  became `type="text"` HH:MM fields. Because that drops the browser's structural enforcement, parsing
  was hardened to strict `TimeOnly.TryParseExact(["H:mm","HH:mm"])` — restoring rejection of
  seconds-bearing (`01:30:00`), single-digit-minute (`2:5`), and bare-minute (`90`) input that
  `type="time"` had made unreachable.

- **The header collapsed from ~5 rows to 2 (`314d14a`).** The stop-list header was five stacked,
  individually-bordered rows with stats split far-left/far-right. It became **one** flex row (title ·
  *N* stops · total travel time inline on the left; Sort + Recompute as bordered icon buttons on the
  right) plus **one** schedule row (Start / Limit / Finish-by), plus the existing conditional OSRM note.
  Sort and Recompute changed from text links to real outlined buttons (`route` / `refresh` icons),
  keeping every handler, `disabled`-while-computing guard, aria-label, and live region. Reclaims roughly
  three rows of vertical height.

- **One reusable HH:MM duration control, uncapped, with steppers.** A new `DurationInput.razor`
  replaced the ad-hoc inputs for **dwell**, **per-leg movement time**, and the trip **Time limit**.
  Masked HH:MM text + ▲▼ stepper buttons: click = ±5 min, **Shift** = ±1 h, floored at 0, hours
  uncapped (so `48:00`/`125:30` round-trip instead of blanking). Steppers are real `<button>`s
  (keyboard-reachable, ArrowUp/Down mirror them) with no JS interop — `ShiftKey` is read straight off
  the Blazor event.

- **Limit and Finish-by became one linked value.** They already both wrote `TimeBudgetMinutes`, but
  were shown as unrelated inputs and Limit was capped ≤24h. Now **Limit is the canonical duration** and
  **Finish-by is a derived view = `start + Limit`**. Editing either reflects the other; changing the
  Start re-derives Finish-by while the budget holds; Finish-by still needs a Start (disabled + hint when
  absent). The uncapped duration now covers the >24h horizon, so Limit no longer renders empty for large
  budgets.

---

## Key decisions & rationale

- **One canonical store for the time budget — never two.** Limit is a thin pass-through to
  `TimeBudgetMinutes`; Finish-by is computed (`start + budget`) for display and back-computes the same
  budget on edit (`finish − start`). This deliberately **supersedes Wave 2's "compute-once deadline"**
  decision (RD10): the deadline is no longer a frozen one-shot — it now re-derives from the live budget
  when the start moves, because a single linked value is what users expect from two views of the same
  thing.

- **Centralize the minutes⇄HH:MM seam.** `TravelTimeFormatting` gained the parse side it lacked:
  `FormatHhmm(int)` (uncapped, `{m/60:D2}:{m%60:D2}`) and strict `TryParseHhmm(string, out int)` (regex
  `^(\d{1,3}):([0-5]\d)$`). This is now the **sole** minutes⇄HH:MM edge; the component and both call
  sites use it, so canonical units (dwell/budget in minutes, leg time in seconds) never leak.

- **Strict parse over lenient — the C# edge is the real gate.** `pattern`/`maxlength`/`inputmode` are
  cosmetic only (Blazor `@onchange` doesn't honor them), so rejection of malformed-but-parseable text
  (`90`, `2:5`, `01:30:00`) lives in `TryParseHhmm`/`TryParseExact`, not the markup.

- **Share the control, but keep it in the Trip slice.** `DurationInput` lives in
  `Components/Shared/Trip/` — extracting it project-wide was explicitly out of scope (Ask-First).

- **No JS for the stepper.** Stepper math (±Step, Shift ±ShiftStep, clamp `[0, Max]`) is pure Blazor,
  reading `ShiftKey` off the event — keeping the component self-contained and testable.

---

## Architecture deltas (vs. before this milestone)

| Area | Before | After |
|------|--------|-------|
| Stop-list header | ~5 stacked bordered rows; stats split far-left/far-right | 2 slim rows (inline stats + bordered Sort/Recompute buttons) + conditional OSRM note |
| Sort / Recompute | `text-primary hover:underline` links | outlined compact icon buttons (`route` / `refresh`); same handlers/guards/aria |
| Duration inputs | dwell HH:MM `type="time"` (AM/PM clock), leg `type=number` minutes, Limit capped ≤24h | one reusable `DurationInput` (masked HH:MM text + ▲▼ steppers, ±5 / Shift ±1h, uncapped) across dwell, per-leg, and Limit |
| Limit ⇄ Finish-by | two inputs writing one field; Limit ≤24h; deadline computed once (frozen) | Limit = canonical duration; Finish-by = derived `start + Limit`; bidirectional edit-time mirroring; re-derives when Start moves |
| HH:MM⇄minutes conversion | display-only `Duration()`; no parse helper | centralized `TravelTimeFormatting.FormatHhmm` + strict `TryParseHhmm` — the single seam |
| New component | — | `Components/Shared/Trip/DurationInput.razor` |
| Copy | `TripTimeLimitAria` said "up to 24h" | "up to 24h" dropped (cap removed) |

**No schema, migration, endpoint, or MCP change** — view-models stay in canonical units, `DurationInput`
raises `ValueChanged` only, and `TravelTimeFormatting` is the lone minutes⇄HH:MM edge. Single Blazor
Server container + SQLite, default `Mock` provider, OSRM still an opt-in sidecar. Mobile
(`MobileTripPanel.razor`) was explicitly out of scope — the mirror remains deferred.

---

## Lessons (from the spec review change-logs)

These changes ran the quick-dev/spec cycle rather than full epics, but each spec's adversarial review pass
still caught a real defect that the green unit suite missed:

- **Dropping a typed control's structural enforcement re-opens its parser.** Swapping `type="time"` →
  `type="text"` silently re-enabled lenient `TimeOnly.TryParse`, which would accept `01:30:00` (seconds
  truncated) and `2:5` (misread as 02:05) — corrupting canonical minutes. Fix: parse strictly with
  `TryParseExact` and tighten the `pattern`. Lesson: when you remove the browser's gate, you own the
  validation in C#.

- **Blazor controlled-input gotcha: a declined write leaves stale text in the DOM.** When `DurationInput`
  (or the derived Finish-by field) refuses to write — rejected parse, clamp-to-equal, or a failed
  finish-by guard — the diff left the user's raw text on screen. Fixed centrally with a re-key
  (`_rev`/`_finishByRev`) so the field always snaps back to the canonical display.

- **Canonical units at the edge — every time.** Both specs changed only input affordances and display;
  the stored `int?`/`DateTime?` fields and the accumulation math were never touched. The linked
  Limit/Finish-by model stores **one** value (the budget) and derives the other view.

- **Run the Trip integration filter after any VM/markup change** (`dotnet test --filter "…~Trip"`) — the
  load-bearing check the unit suite alone can't give, especially since the shared layer reaches mobile.

---

## Known follow-ups (carried tech-debt)

- **Mirror-to-mobile still deferred.** `MobileTripPanel.razor` keeps its own numeric-minutes dwell input
  and `type="time"` start, and does **not** use `DurationInput` or the linked Limit/Finish-by model. The
  desktop/mobile divergence is intentional for now; the mobile mirror remains a future phase (carried
  forward from Wave 2).
- **`DurationInput` is Trip-scoped by design.** If another surface needs an HH:MM duration picker,
  promoting it out of `Components/Shared/Trip/` is the Ask-First extraction point.
- Wave 2's carried debt is unchanged by this milestone (per-leg Manual-override orphan on mode change;
  `PoiCollection.TravelMode` dead column) — see
  [trip-view-realignment-feature-summary.md](trip-view-realignment-feature-summary.md).

---

## Verification at close

Both specs ship `done`, merged to `master` (clean tree). Stated verification per spec:
`dotnet build LucidCartographer.sln` clean under `TreatWarningsAsErrors`, and
`dotnet test --filter "FullyQualifiedName~Trip"` green — including new `DurationInput` stepper/parse/
revert-on-invalid tests, `FormatHhmm`/`TryParseHhmm` unit tests, the uncapped `48:00` Time-limit render,
and the Limit⇄Finish-by reflection + Start-change re-derivation assertions. Strict-parse theories reject
`90` / `2:5` / `01:30:00` / `abc` on both dwell and Time-limit. The existing dwell HH:MM round-trip and
reject tests stay green (the control still renders `type="text"`).
