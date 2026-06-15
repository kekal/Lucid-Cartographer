# Story 4.3: Time limit as duration or finish-by deadline, with "Over limit"

Status: ready-for-dev

## Story

As a trip planner, I want to set how long the whole trip should take — as a length or a "done by"
deadline — and be warned when I exceed it, so that I can keep a multi-day plan within a goal I
actually think in.

## Acceptance Criteria

1. **Given** the time-limit control (renamed from "Time budget" to "Time limit"; overrun "Over budget" → "Over limit" in `UiStrings`), **When** I enter a limit as an HH:MM duration, **Then** it persists as the canonical `TimeBudgetMinutes` (HH:MM ↔ minutes at the UI edge only); no schema change (FR-28, NFR2).
2. **Given** I instead pick a finish-by deadline (date+time, requires a start), **When** the limit is set, **Then** the app computes it **once** as `deadline − start` and stores it as the fixed-goal `TimeBudgetMinutes`; it does **not** recompute when the start or the trip later changes (TRIP-SCHEDULE-01, FR-29).
3. **Given** a limit is set and the trip total exceeds it, **When** the panel renders, **Then** an "Over limit" indicator shows as an amber soft-warn (`text-amber-600`), never red / `{colors.tertiary}`; it is informational and non-blocking, and absent when no limit is set (FR-28, UX-DR8); **And** the finish-by deadline is distinct from the Finish stop of Story 4.5 (a time goal, not an end POI) (FR-29).
4. **Given** the native HH:MM duration input caps at 24h, **When** a multi-day limit (>24h) is needed, **Then** the finish-by-deadline path covers it; the HH:MM duration entry is scoped to ≤24h limits and multi-day users are steered to the deadline path. *(Readiness §4 MEDIUM — explicit >24h coverage.)*

## Architecture & Code Context (RD10, FR-28/29, TRIP-SCHEDULE-01, UX-DR8)

**File:** `LucidCartographer/Components/Shared/Trip/TripStopList.razor` (desktop). Today the budget is
an `<input type="number">` in minutes (`TripBudgetLabel`/`TripBudgetAria`/`TripBudgetPlaceholder`,
`BudgetValue()`, `OnBudgetChangedAsync`), with the amber overrun chip gated on
`Vm.Timeline.IsOverBudget` (`TripBudgetOverrunLabel`/`TripBudgetOverrunAria`). Persistence:
`Vm.TimeBudgetMinutes` + `Vm.SetTimeBudgetMinutesAsync(int?)` (canonical minutes, exists). The
timeline already computes `IsOverBudget` (total ≥ budget×60). **No schema change.**

**Required (desktop):**
1. **Rename copy** to "Time limit" / "Over limit". Per the architecture's naming, add `UiStrings`
   keys `TripTimeLimit*` (label/aria/placeholder) and `TripOverLimit*` (label/aria) with the new
   text; repoint the desktop control + the overrun chip. (Keep the OLD `TripBudget*` keys only if
   mobile still references them — repoint mobile's labels too if trivial, or leave mobile on the old
   keys for the deferred mirror; do NOT break mobile. Prefer: rename the values the desktop uses;
   mobile keeps working.)
2. **Duration entry (HH:MM ≤24h):** replace the raw-minutes number input with an HH:MM duration
   picker (native `<input type="time">`, capped at 23:59 by the control). Convert HH:MM ↔ minutes at
   the UI edge: value = `TimeBudgetMinutes` → `HH:mm` (hours = m/60, mins = m%60, only when ≤24h);
   on change parse `HH:mm` → minutes → `SetTimeBudgetMinutesAsync`. Empty clears (null).
3. **Finish-by deadline alternative (FR-29, TRIP-SCHEDULE-01):** add a `datetime-local` "finish by"
   input next to the duration. It REQUIRES a start (`Vm.TripStartTime`): on change, compute
   `minutes = (int)Math.Round((deadline - Vm.TripStartTime.Value).TotalMinutes)` ONCE (guard ≥ 0 and
   ≤ `MaxBudgetMinutes`), call `SetTimeBudgetMinutesAsync(minutes)`, and do NOT persist the deadline
   itself — only the resulting minutes. So it never recomputes when start/trip later change
   (TRIP-SCHEDULE-01). When no start is set, disable/explain the deadline input (it needs a start).
   This is the >24h path (AC4): a deadline can be days out, yielding a >24h `TimeBudgetMinutes`.
4. **Over-limit chip:** keep gating on `Vm.Timeline.IsOverBudget`; rename to "Over limit"; keep the
   amber soft-warn tone (`text-amber-600` on `surface-container`), never red/tertiary (UX-DR8).
5. **No VM math change** beyond reusing `SetTimeBudgetMinutesAsync` — the deadline→minutes and
   HH:MM↔minutes conversions are UI-edge (component bridge), consistent with the existing dwell/budget
   bridges (NFR1/NFR2). `TimeBudgetMinutes` stays the canonical fixed-goal minutes.

**Mobile:** the budget control on `MobileTripPanel` is the deferred mirror — leave its number input
(or minimally rename its label) and do not break mobile tests. The duration/deadline pickers are
desktop now.

## Constraints (NFRs)

- NFR2 — `TimeBudgetMinutes` stays canonical minutes; HH:MM and deadline→minutes convert only at the
  UI edge; deadline computed ONCE (TRIP-SCHEDULE-01), never recomputed.
- NFR6 — copy via `UiStrings` (new `TripTimeLimit*`/`TripOverLimit*`); amber soft-warn token, never
  red/tertiary.
- NFR1 — conversions in the component bridge; no service math change.

## Testing

- bUnit / component: the time-limit duration input is HH:MM and round-trips `TimeBudgetMinutes`
  (≤24h); entering HH:MM persists the right minutes; the finish-by `datetime-local` (with a start
  set) computes `deadline − start` minutes ONCE and persists it, including a **>24h** case (multi-day
  deadline → >1440 minutes); the deadline input requires a start (disabled/explained when none); the
  stored limit does NOT change when the start is later changed (compute-once). The "Over limit" chip
  shows amber (no red/tertiary class) when total > limit and is absent with no limit. Copy reads
  "Time limit"/"Over limit".
- Unit (edge conversions if extracted): HH:MM↔minutes and deadline−start→minutes.
- Trip integration filter green; mobile trip tests green.

## Build/Test commands

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Trip integration: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`
- Mobile: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Mobile"`

## Dev Notes

The finish-by DEADLINE (a time goal, computed once into minutes) is distinct from the Finish STOP
(Story 4.5, an end POI). Keep them clearly separate in copy. Dwell HH:MM is Story 4.4.

## Dev Agent Record

(to be filled)

## File List

(to be filled)
