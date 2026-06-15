---
baseline_commit: ea8eb3d05e2402db76926e3ecd60f22c46f36e88
---

# Story 2.6: Compute the honest Itinerary Timeline

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a trip planner,
I want a running timeline that tells me when I arrive where and never fakes precision,
so that I can judge whether the day fits and trust the numbers.

## Acceptance Criteria

_(Source: epics.md → Epic 2 → Story 2.6; FR-13, NFR10, UX-DR6, UX-DR10, UX-DR11, AR-11)_

1. **Timeline walk (FR-13).** Over the placeable stops in Stop Order with their travel + dwell times, the timeline computes: `arrival(1) = TripStart` (or relative offset 0); `departure(k) = arrival(k) + Dwell(k)`; `arrival(k+1) = departure(k) + TravelTime(k→k+1)`. The Start's dwell is counted once at the beginning. A **Roundtrip** produces a distinct **return-to-Start** arrival via the closing leg's travel time (open path ends at the Finish, no return).

2. **Relative offset always; wall-clock only with a start time (UX-DR6).** Each stop shows a relative cumulative offset (e.g. `+2h15m`), always. A wall-clock arrival (e.g. `14:10`) shows **only** when the trip has a `TripStartTime` set. A finish/return readout appears at the end. A per-trip `TripStartTime` is settable (persisted on `PoiCollection.TripStartTime`).

3. **Aggregate honesty — inherit the lowest fidelity (UX-DR6, NFR10).** A running arrival/total inherits the **lowest** fidelity among the legs it sums and never shows a clean confident time over mixed fidelity. Ranking (least→most trusted): **unknown (Placeholder or uncomputed) < Estimated < Manual ≈ Measured (confident)**. So a cumulative whose summed legs are all Measured/Manual renders a clean time (`14:10`); if any summed leg is **Estimated**, it renders qualified (`~14:10 · Estimated`); if any summed leg is **Placeholder or uncomputed**, the arrival is **unknown** — rendered `—` (em-dash) — and that uncertainty **propagates** to every downstream arrival and to the finish/return + total.

4. **Edge cases (FR-13, UX-DR10).** An **Unplaceable** stop contributes its Dwell to the trip total but adds **no** travel time (it has no leg). A **Placeholder**-fidelity leg propagates uncertainty downstream (per AC3). With **no** `TripStartTime`, only relative offsets show (no wall-clock). With a `TripStartTime`, wall-clock arrivals show alongside offsets.

5. **Soft time-budget overrun (UX-DR6).** An optional per-trip time budget is settable (persisted on `PoiCollection.TimeBudgetMinutes`). When the computed trip total exceeds the budget, a **soft `warn`** (amber, **not** `tertiary`/red) overrun flag is shown. With **no** budget set, **no** flag is ever shown. (If the total is uncertain — any unknown leg — the overrun cannot be asserted; do not show a false overrun.)

6. **Off-thread / honesty / voice.** The timeline derives from the already-loaded stops/dwell/legs + the collection's `TripStartTime`/`TimeBudgetMinutes`; it recomputes via the existing `RefreshProjectionsAsync`/`StateChanged` path (no polling, no new background plumbing). All copy is honest and via `UiStrings` (provenance qualifier on uncertain/estimated values; `—` for unknown). Both surfaces. Build warnings-as-errors, no group-B analyzer violations, no `ConfigureAwait(false)`; new decisions tagged `TRIP-TIMELINE-01`; canonical units (seconds/minutes internally, formatted at the UI edge); no new migration (`TripStartTime`/`TimeBudgetMinutes` already exist).

## Tasks / Subtasks

- [x] **Task 1 — Pure timeline computation (AC: 1, 3, 4, 5)**
  - [x] Add `ItineraryTimeline` (static/`internal`, `Services/Trip/`) — a **pure** function `Compute(...)` taking: the ordered placeable stops (each with `PoiId`, `DwellMinutes?`), the ordered legs (each with `DurationSeconds?` and `Fidelity?` — null/Placeholder = unknown), the unplaceable stops' `DwellMinutes?`, `bool isRoundtrip`, `DateTime? tripStart`, `int? budgetMinutes`. Returns an immutable result: a per-stop list of `{ PoiId, OffsetSeconds?, ArrivalWallClock (DateTime?), QualifyingFidelity (string?), IsUnknown }`, a finish/return entry, `TotalSeconds?` (+ qualifying fidelity / IsUnknown), and `IsOverBudget` (bool, only when budget set AND total known). Tag `// TRIP-TIMELINE-01`.
  - [x] Walk per AC1: relative offsets in seconds; Start dwell once at the start; `departure = arrival + dwell`, next `arrival = departure + travel`. Roundtrip adds a return arrival via the closing leg; open path ends at the last stop.
  - [x] Honesty (AC3): track the running **minimum** fidelity rank across the legs summed so far (`Unknown=0 < Estimated=1 < Manual=2 ≈ Measured=2`). Once a leg is **Unknown** (Placeholder or null duration), every arrival from that point on is `IsUnknown = true` (no offset/wall-clock), and so are the finish/return + total. A surviving `Estimated` sets the qualifier to Estimated; all-Manual/Measured ⇒ no qualifier (clean).
  - [x] Unplaceable dwell (AC4): add each unplaceable stop's dwell to `TotalSeconds` (no leg, no per-stop arrival in the routed sequence). Document this interpretation in the XML doc.
  - [x] Budget (AC5): `IsOverBudget = budgetMinutes is set AND total is known AND total > budget*60`. Never assert overrun on an unknown total.
  - [x] Convert minutes↔seconds only here (dwell/budget are minutes; travel is seconds) — canonical seconds internally (AR-11).

- [x] **Task 2 — Persist TripStartTime + budget; expose timeline in the VM (AC: 2, 5, 6)**
  - [x] Add `SetTripStartTimeAsync(DateTime? start)` and `SetTimeBudgetMinutesAsync(int? minutes)` to `TripViewModel`, mirroring the dwell/mode persistence (write `PoiCollection.TripStartTime`/`TimeBudgetMinutes` under the write lock, refresh, `Notify`). Range-guard the budget (`>= 0`, `<= MaxDwellMinutes`-style bound). Do NOT signal the travel-time trigger (these don't affect route segments).
  - [x] In `RefreshProjectionsAsync`, after legs/stops/dwell are read, compute `ItineraryTimeline.Compute(...)` and expose it as VM state (e.g. `public ItineraryTimelineResult Timeline { get; private set; }` plus `TripStartTime`/`TimeBudgetMinutes` for the inputs' active values). Read `TripStartTime`/`TimeBudgetMinutes` alongside the existing `ReadTravelModeAsync` collection read. Recompute is presentation-only (no extra DB round-trips beyond reading the two fields).
  - [x] The timeline must recompute whenever legs change (background compute lands a row → existing progress→`RefreshLegsFromCacheAsync` → rebuild legs → recompute timeline → `StateChanged`). Ensure the progress-refresh path also recomputes the timeline (both refresh paths).

- [x] **Task 3 — Timeline UI, both surfaces (AC: 2, 3, 4, 5, 6)**
  - [x] Per stop: render the arrival as `+Hh Mm` (offset, always) and, when `TripStartTime` set, the wall-clock `HH:MM`; qualified per fidelity (`~…· Estimated`, or `—` when unknown). Place it as a compact read-only value in the stop row (no input ⇒ no selection risk; keep it narrow to avoid the 2.2/2.5 panel-width regression — verify `TripView_ShowsStopListPanel_BesideMap…` stays green).
  - [x] At the end of the list: a **finish/return** readout (the return-to-Start arrival for a Roundtrip, or the Finish arrival for an open path), qualified.
  - [x] Header area: a `TripStartTime` input (time/datetime, `UiStrings`-labelled) wired to `SetTripStartTimeAsync`, and a time-budget (minutes) input wired to `SetTimeBudgetMinutesAsync`. A soft **`warn`/amber** overrun flag (a small badge/note via `UiStrings`) when `Timeline.IsOverBudget`. NOT red. Place inputs/flag in the header, not in the clickable rows.
  - [x] All copy via `UiStrings` (offset/wall-clock/qualifier/finish/return labels + arias, overrun note, start-time + budget labels). Format with `CultureInfo.CurrentCulture`. Both surfaces.

- [x] **Task 4 — Tests (AC: all)**
  - [x] **Unit (computation) — the heart, exhaustive:**
    - Basic walk: 3 placeable stops, dwell + known (Measured/Manual) legs ⇒ correct offsets + wall-clock (with start) + clean (no qualifier); Start dwell counted once.
    - Roundtrip vs open path: roundtrip yields a return-to-Start arrival via the closing leg; open path ends at the Finish (no return).
    - Mixed fidelity: an Estimated leg ⇒ that arrival and all downstream qualified `Estimated`; total qualified Estimated.
    - Unknown propagation: a Placeholder/uncomputed leg ⇒ that arrival + all downstream + finish/return + total are `IsUnknown` (no offset/wall-clock), nothing upstream affected.
    - Unplaceable dwell: contributes to total, no per-stop arrival, no travel time.
    - No start time ⇒ offsets only, wall-clock null; with start ⇒ wall-clock present.
    - Budget: total > budget ⇒ `IsOverBudget` true; total ≤ budget ⇒ false; no budget ⇒ false; unknown total ⇒ false (never a false overrun).
  - [x] **Unit/VM:** `SetTripStartTimeAsync`/`SetTimeBudgetMinutesAsync` persist to `PoiCollection` + round-trip; range guard; do NOT signal the trigger; the VM `Timeline` reflects seeded stops/dwell/legs.
  - [x] **Component (bUnit), both surfaces:** per-stop offset renders; wall-clock appears only with a start time; an Estimated arrival shows the qualifier; an unknown arrival shows `—`; the finish/return readout renders; the overrun flag shows only when over budget (amber, not red); start-time + budget inputs render and invoke the VM. Selection still works (read-only timeline value in rows).
  - [x] Full unit/component suite green; **Trip integration green** (selection + panel-beside-map); no new analyzer warnings.

## Dev Notes

### Scope guardrails
- **In scope:** the pure timeline computation (walk + aggregate-honesty + uncertainty propagation + unplaceable dwell + budget overrun), `TripStartTime` + budget persistence/inputs, per-stop arrival + finish/return display + soft overrun flag (both surfaces), exhaustive computation tests.
- **OUT of scope:** anything Epic 3+ (TSP-Sort, MCP); road geometry / OSRM (Epic 4); changing travel-time computation, caching, or the provider; new migration. This is the last Epic 2 story — it consumes 2.1–2.5, it does not modify them.

### The honesty rule is the point (NFR10 / UX-DR6/DR11)
This story's whole value is **not faking precision**. Get the fidelity propagation exactly right:
- Fidelity rank: **Unknown (Placeholder or null-duration) = 0**, **Estimated = 1**, **Manual = 2**, **Measured = 2** (Manual and Measured are both "confident" — neither adds a qualifier).
- A cumulative arrival's qualifier = the lowest rank among ALL legs summed up to it.
- Rank 0 anywhere upstream ⇒ this and every downstream arrival is `—` (unknown), because an unknown leg duration makes the arrival genuinely uncomputable — never guess.
- Reuse `Fidelity` constants (`Data/Entities/Fidelity.cs`) and `TripLeg.DurationSeconds`/`Fidelity` (a 2.2 Placeholder leg already nulls its display duration — so a Placeholder leg presents as `DurationSeconds == null`, i.e. unknown — confirm and rely on this).

### Built on prior stories (reuse, don't reinvent)
- `PoiCollection.TripStartTime` (`DateTime?`, `:51`) + `TimeBudgetMinutes` (`int?`, `:54`) already exist — no migration.
- `TripViewModel`: `OrderedStops` (placeable, in order), `OrderedLegs` (`TripLeg` with `DurationSeconds?`/`Fidelity`/`IsMeasured`), `StopRows` (carry `DwellMinutes` from 2.5, incl. unplaceable). `IsRoundtrip` already exists (FinishPoiId null). The dwell/mode/start persistence pattern: mirror `SetDwellMinutesAsync` (2.5) / `SetTravelModeAsync` (2.2) — write under `SqliteWriteLock`, refresh, Notify, NO trigger signal.
- `RefreshProjectionsAsync` (`:659`) + the progress→`RefreshLegsFromCacheAsync` subscription (2.1) — compute the timeline in BOTH so a background leg landing updates arrivals via `StateChanged`. The 2.2 AC4 fix: a Placeholder leg has `DurationSeconds == null` ⇒ treat as Unknown in the timeline (consistent).
- `TravelTimeFormatting` (`Services/Trip/`) — reuse for the offset (`+Hh Mm`); add wall-clock + qualifier formatting via `UiStrings` (no hardcoded patterns — the 2.1 review lesson).
- The 2.5 dwell input + 2.2 manual input show how a row stays selection-safe; the timeline value is **read-only text** (no input) so no `stopPropagation` needed — but keep it NARROW (the 2.5 `w-10` lesson: a too-wide row element collapses the name span and trips the panel-beside-map test).

### Architecture & conventions (project-context.md)
- Layering: the computation is a pure Service-layer function; the VM holds the result; the `.razor` renders it + binds the two inputs to VM methods. No compute in markup.
- Build discipline: warnings-as-errors, no group-B analyzer violations, no `ConfigureAwait(false)`, `CultureInfo.CurrentCulture` on all formatting. Tag `TRIP-TIMELINE-01`.
- i18n: every label/qualifier/`—`/overrun string via `UiStrings`. a11y: arrival values + overrun in `aria-label`led / `aria-live` regions as appropriate; both desktop and `Mobile*`. Soft overrun uses the `warn`/amber treatment (UX-DR6) — there is no `warn` Tailwind token (2.3 finding), so use the muted/amber convention the project already uses for soft states; never `tertiary`/red.
- Units: seconds internally; dwell/budget minutes→seconds at the computation edge; format at the UI edge only (AR-11).
- The VM ctor is unchanged (computation is static; no new dependency) — no test-builder/DI churn.

### Testing standards
- The computation is pure ⇒ test it exhaustively as plain unit tests (no DB) — this is where the honesty rule is proven. Then VM persistence (EF InMemory/temp-SQLite) + bUnit display both surfaces (`MobileTestBase`). Keep the Trip integration selection + panel tests green. `InternalsVisibleTo` set.

### Previous-story intelligence
- 2.1: route all formatted strings through `UiStrings` + `CultureInfo`; give every value an honest `aria-label` (no leftover wrong string).
- 2.2/2.5: a wide element in the stop row collapses the name span and breaks the panel-beside-map integration test — keep the per-stop arrival narrow; put the start-time/budget inputs + overrun flag in the header, not the rows.
- 2.2: a Placeholder leg presents `DurationSeconds == null` — the timeline treats null-duration legs as Unknown uniformly (covers Placeholder + genuinely-uncomputed).
- Assert the computed values + qualifiers + IsUnknown/IsOverBudget directly (pure-function tests), not just that a method was called.

### References
- [Source: _bmad-output/planning-artifacts/epics.md#Story 2.6] — FR-13, NFR10, UX-DR6, UX-DR10, UX-DR11
- [Source: _bmad-output/planning-artifacts/epics.md#Additional Requirements] — AR-11 (units: dwell/budget minutes, travel seconds, convert at edge)
- [Source: _bmad-output/project-context.md]
- [Source: LucidCartographer/Data/Entities/PoiCollection.cs:51,54], [Fidelity.cs]
- [Source: LucidCartographer/Components/Shared/Trip/TripViewModel.cs:659 (RefreshProjectionsAsync), IsRoundtrip, OrderedStops/OrderedLegs/StopRows; SetDwellMinutesAsync (2.5) / SetTravelModeAsync (2.2) persistence pattern], [TripProjections.cs (TripLeg/TripStopRow)], [Services/Trip/TravelTimeFormatting.cs]
- [Source: LucidCartographer/Components/Shared/Trip/TripStopList.razor], [MobileTripPanel.razor], [Services/UiStrings.cs]

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (BMAD dev-story; delegated dev subagent + orchestrator verification + fresh-context honesty review + fixes)

### Debug Log References

- Build: 0 warnings / 0 errors.
- Unit/component (`!~Integration`): **698 passed** (incl. exhaustive pure-computation honesty tests).
- Trip integration (`Integration&Trip`): **19 passed** (selection + panel-beside-map green with the per-stop arrival added).

### Completion Notes List

- ✅ AC1 — `ItineraryTimeline.Compute` walks `arrival/departure` correctly: Start dwell once; `departure=arrival+dwell`; `arrival=departure+travel`; roundtrip return-to-Start via the closing leg; open path ends at Finish (last dwell counted on roundtrip, not open path). Tests `BasicWalk_OpenPath…`, `Roundtrip_ProducesDistinctReturnToStartArrival…`.
- ✅ AC2 — relative offset always; wall-clock only with `TripStartTime`; finish/return readout; `TripStartTime` persisted + settable. Tests + bUnit `…RendersOffset_Always_WallClockOnlyWithStart`.
- ✅ AC3 — **fidelity propagation verified correct**: rank Unknown(0)<Estimated(1)<Manual≈Measured(2), running `Math.Min`; an Unknown (Placeholder/null-duration) leg makes that + all downstream + finish/return + total `IsUnknown` (`—`), upstream untouched; Estimated qualifies; Manual/Measured clean. No path treats an unknown duration as 0. Tests `PlaceholderLeg_Makes…Unknown`, `NullDurationLeg_IsUnknown`, `EstimatedLeg_Qualifies…`, `ManualLeg_IsConfident_NoQualifier`.
- ✅ AC4 — unplaceable dwell adds to total only (no arrival/travel) and does not resurrect an unknown total (`UnplaceableDwell_OnUnknownTotal_DoesNotResurrectTheTotal`).
- ✅ AC5 — soft amber overrun only when budget set AND total known AND over; never on an unknown total; `warn` token (amber, not red). Tests `Budget_*`, `Timeline_UnknownTotal_NeverFalseOverrun…`.
- ✅ AC6 — recomputes in both `RefreshProjectionsAsync` and the progress→`RefreshLegsFromCacheAsync` path; persistence doesn't signal the trigger; both surfaces; no migration.
- Review (fresh context): **0 CRITICAL / 0 HIGH / 1 MEDIUM / 4 LOW**. Core math + honesty rule confirmed correct. Fixes applied:
  - [x] [MEDIUM] In the default offset-only mode (no trip start), `ArrivalCompact` showed an Estimated arrival as a bare `+1h 0m` — visually identical to a confident time (the "~" was only applied to the wall-clock). **Fixed:** the "~" approximation prefix is now applied to the estimated offset too, so an estimated arrival is never a clean confident offset. Guarded by a strengthened render test asserting the `~`-prefixed offset on the arrival value (not just the badge).
  - [x] [LOW] Roundtrip predicate divergence: the timeline used `IsRoundtrip` (FinishPoiId null) while `BuildLegs` keys off the live-Finish predicate — a stale/Start-equal Finish could mismatch the total. **Fixed:** `RecomputeTimeline` now derives the closing-leg flag from the actual leg set (`legs.Count >= stops.Count`), keeping the timeline total consistent with the rendered legs.
  - [x] [LOW] Strengthened the masking render test (now asserts the arrival element's `~` marker, not just the independently-rendered badge).
  - [x] [LOW] Dropped the dead raw-hex fallbacks on the mobile overrun chip (`--warn`/`--warn-soft` are always defined) — matches the "tokens only" comment + the desktop surface.
  - [ ] [LOW, n/a] Story checkboxes/File List were blank at review — populated here (orchestrator-owned).

**Deviations / decisions:**
- Unplaceable dwell → total only (no arrival/leg); the last placeable stop's dwell counts on a Roundtrip (dwell before departing home) but not on an open path. Documented in the `Compute` XML doc.
- Fidelity rank: Manual = Measured = 2 (both confident, no qualifier) per the story.
- Amber overrun: no `warn` Tailwind token exists; desktop uses `text-amber-600` on `bg-surface-container`, mobile uses the `--warn`/`--warn-soft` CSS vars — never `tertiary`/red (asserted in tests).
- Row width (2.2/2.5 lesson): the per-stop arrival is a compact read-only value stacked in the existing leg-time column (no new horizontal element; `gap-2`→`gap-1`); the verbose "· Estimated" word is dropped in the row (the "~" marker + per-leg badge + full-text `title`/`aria` carry provenance) while the finish/return readout uses the full qualified text. Panel-beside-map + selection integration tests confirm the name span doesn't collapse.
- This is the final Epic 2 story — it consumes 2.1–2.5, does not modify their behavior; no scope leak into Epic 3/4; no migration.

### File List

**New (4):**
- `LucidCartographer/Services/Trip/ItineraryTimeline.cs` (pure `Compute` + result/input records)
- `LucidCartographer.Tests/Services/ItineraryTimelineTests.cs`
- `LucidCartographer.Tests/ViewModels/TripViewModelTimelineTests.cs`
- `LucidCartographer.Tests/Components/Trip/TripTimelineRenderTests.cs`

**Modified (5):**
- `LucidCartographer/Components/Shared/Trip/TripViewModel.cs` (`Timeline`/`TripStartTime`/`TimeBudgetMinutes` state; `SetTripStartTimeAsync`/`SetTimeBudgetMinutesAsync` + `MaxBudgetMinutes`; `ReadTripSettingsAsync`; `RecomputeTimeline` in both refresh paths; closing-leg flag from legs)
- `LucidCartographer/Services/Trip/TravelTimeFormatting.cs` (`Arrival` + `ArrivalCompact`, with the offset `~` fix)
- `LucidCartographer/Components/Shared/Trip/TripStopList.razor` (header start-time/budget inputs, overrun flag, per-stop arrival, finish/return)
- `LucidCartographer/Components/Shared/Trip/MobileTripPanel.razor` (same, mobile)
- `LucidCartographer/Services/UiStrings.cs` (timeline/start-time/budget/overrun strings)

## Change Log

| Date | Change |
|------|--------|
| 2026-06-14 | Story 2.6 implemented: pure `ItineraryTimeline.Compute` (walk + aggregate-honesty/lowest-fidelity + unknown-propagation + unplaceable dwell + soft budget overrun), `TripStartTime`/`TimeBudgetMinutes` persistence + header inputs, per-stop arrival (offset always, wall-clock with start, qualified) + finish/return on both surfaces. No migration. Build clean; 698 unit/component + 19 Trip integration green. Status → review. |
| 2026-06-14 | Adversarial honesty review (fresh context): 0 CRITICAL/0 HIGH/1 MEDIUM/4 LOW; core fidelity-propagation math confirmed correct. Fixed the compact-row offset honesty gap ("~" on estimated offset) + roundtrip-from-legs + strengthened test + dropped dead hex. 698 unit/component + 19 Trip integration green. Status → done. |

## Senior Developer Review (AI)

**Outcome:** Approve (done) — 0 CRITICAL / 0 HIGH; 1 MEDIUM + 3 actionable LOW fixed, 1 LOW (doc) n/a.
**Reviewer:** Fresh-context adversarial reviewer (separate agent from the implementer).
**Date:** 2026-06-14

**Explicit verdicts:**
- **Fidelity propagation (the honesty core): CORRECT.** Unknown(0)<Estimated(1)<Manual≈Measured(2), running min; an unknown leg poisons that + all downstream + finish/return + total (no offset/wall-clock, `—`), upstream untouched; no path treats an unknown duration as 0; budget never asserts a false overrun on an unknown total.
- **Compact-row honesty: now sound.** The MEDIUM (estimated offset shown without a marker in offset-only mode) is fixed — the `~` approximation marker is applied to the offset; dropping only the verbose word (badge + full aria retain the named provenance) is an acceptable width trade.

**Findings:** [MEDIUM] compact estimated-offset marker — **fixed**. [LOW] roundtrip predicate divergence — **fixed** (derive from legs). [LOW] masking render test — **fixed** (assert the arrival marker). [LOW] mobile overrun hex fallback — **fixed** (dropped). [LOW] story doc tracking — populated. All 6 ACs hold; honesty rule correctly implemented; no scope leakage; no migration.
