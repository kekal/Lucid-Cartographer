---
title: 'Trip stops panel — header compaction & unified duration pickers'
type: 'feature'
created: '2026-06-16'
status: 'done'
baseline_commit: '88d04873079b69ede1d6401c6d804a5fff3c459e'
context:
  - '{project-root}/_bmad-output/planning-artifacts/ux-designs/ux-maps_editor-2026-06-16/EXPERIENCE-delta.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The desktop Trip View stop-list header wastes vertical height on five
stacked rows; stat labels/values are split far-left/far-right; Sort & Recompute are
text links; duration fields are inconsistent (dwell HH:MM capped at 24h, leg in raw
minutes); and Limit vs Finish-by are presented as two unrelated inputs though they
are the same underlying value.

**Approach:** Collapse the header to two slim rows (stats+buttons; schedule) plus the
existing conditional OSRM note. Make Sort/Recompute real buttons. Introduce one
reusable HH:MM duration control with ▲▼ steppers (±5 min, Shift ±1h, hours uncapped,
floor 0) and use it for dwell, per-leg movement, and the time Limit. Link Limit and
Finish-by as two views of the canonical time-budget (minutes): editing one reflects
the other; Finish-by is a derived `start + Limit` display. Desktop only.

## Boundaries & Constraints

**Always:** Keep all existing `UiStrings`, `aria-label`s, and `role=status`/`aria-live`
regions intact (relocation is fine). Canonical units unchanged: dwell & budget in
minutes, leg time in seconds. HH:MM↔unit conversion stays at the UI edge; VM keeps
canonical values. Strict parse: reject input without exactly `H..HHH:MM` (minutes
`00–59`). Clamp steps to `[0, Max]`. Preserve per-leg click-to-edit + Manual reset.

**Ask First:** Extracting `DurationInput` beyond the Trip slice (keep it in
`Components/Shared/Trip/`); any change to mobile (`MobileTripPanel.razor`); renaming
the visible "Time limit"/"Over limit" copy.

**Never:** Touch mobile dwell/budget inputs, leg distance, fidelity badge, travel-mode
pill, drag/reorder, or selection behavior. No JS interop for the stepper. Do not add a
second store for the deadline — Finish-by derives from the budget.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Format canonical | 2880 min | renders `48:00` (uncapped, not blank) | N/A |
| Format sub-hour | 45 min | renders `00:45` | N/A |
| Parse uncapped | type `125:30` | persists 7530 min | N/A |
| Reject malformed | `90` / `2:5` / `01:30:00` / `abc` | no write; value unchanged | swallow, keep last good |
| Step up | value 45, click ▲ | 50 min (`00:50`) | N/A |
| Shift-step | value 45, Shift+▲ | 105 min (`01:45`) | N/A |
| Step floor | value 0, click ▼ | stays 0 | clamp |
| Step ceiling | value at Max, click ▲ | stays Max | clamp |
| Clear | empty the field | persists null (budget/dwell) or clears manual leg | N/A |
| Limit→Finish | start 09:00, Limit `06:00` | Finish-by shows 15:00 | N/A |
| Finish→Limit | start 09:00, Finish 13:00 | Limit shows `04:00`, budget 240 | N/A |
| Start moves | budget 240, start 09:00→10:00 | Finish-by re-derives to 14:00; budget stays 240 | N/A |
| Finish no start | start unset | Finish-by disabled + needs-start hint | N/A |

</frozen-after-approval>

## Code Map

- `Services/Trip/TravelTimeFormatting.cs` -- has `Duration()` format side; ADD uncapped `FormatHhmm(int)` + `TryParseHhmm(string,out int)` (no parse side today).
- `Components/Shared/Trip/DurationInput.razor` -- NEW reusable HH:MM+stepper control.
- `Components/Shared/Trip/TripStopList.razor` -- header (L13–133), dwell inputs (L246–251, L360–367), helpers `DurationValue()` L564, `OnFinishByChangedAsync` L619, `OnDwellMinutesChangedAsync` L661.
- `Components/Shared/Trip/LegConnector.razor` -- per-leg edit input (L24–33), `OnManualMinutesChangedAsync` L122.
- `Services/UiStrings.cs` -- `TripTimeLimitAria` L322 ("up to 24h" copy).
- `LucidCartographer.Tests/Components/Trip/TripTimelineRenderTests.cs`, `TripStopListTests.cs` -- render tests; `Services/.../TravelTimeFormatting`-area tests.

## Tasks & Acceptance

**Execution:**
- [x] `Services/Trip/TravelTimeFormatting.cs` -- add `FormatHhmm(int minutes)` (`{m/60:D2}:{m%60:D2}`, hours uncapped) and `TryParseHhmm(string, out int minutes)` (regex `^(\d{1,3}):([0-5]\d)$`, rejects no-colon/3-digit-min/seconds). Centralize the conversion both inputs and the component use.
- [x] `Components/Shared/Trip/DurationInput.razor` -- NEW. Params: `Value` (int? minutes), `ValueChanged` (EventCallback<int?>), `AriaLabel`, `Placeholder`="hh:mm", `Max` (int?), `Step`=5, `ShiftStep`=60, `Disabled`, `AutoFocus`, `OnBlur`, `InputClass`. Renders `<input type="text" inputmode="numeric">` showing `FormatHhmm(Value)` (empty when null) + ▲▼ `<button>`s. Typing: on change parse via `TryParseHhmm`; valid → `ValueChanged(parsed)`, blank → `ValueChanged(null)`, invalid → no write (re-render restores). Stepping: `@onclick`/`@onkeydown` (ArrowUp/Down) read `ShiftKey`; new = clamp(`Value??0` ± step, 0, `Max`); invoke `ValueChanged`. Steppers are real buttons with `aria-label`. No JS.
- [x] `Components/Shared/Trip/TripStopList.razor` -- (1) Merge header rows a+b+c (L13–66) into ONE flex row: left = `Trip stops · {OrderedStops.Count} stops · {Duration(TotalTravelTimeSeconds)}` inline (keep count live-region + total aria); right = Sort + Recompute as bordered icon buttons (keep disabled/handlers/aria + LastSortAnnouncement). (2) Replace both dwell `<input>`s with `<DurationInput Value=DwellMinutesRaw(row) ValueChanged=... Max=TripViewModel.MaxDwellMinutes AriaLabel=... />` keeping stopPropagation on the placeable row. (3) Replace the Limit `<input>` with `<DurationInput Value=Vm.TimeBudgetMinutes ValueChanged=OnBudgetChangedAsync Max=TripViewModel.MaxBudgetMinutes AriaLabel=... />`; drop `DurationValue()`'s `<=1440` cap (helper now unused → remove). (4) Add `FinishByValue()` = `start.AddMinutes(budget)` formatted, bind `value="@FinishByValue()"` on Finish-by; keep `OnFinishByChangedAsync` (deadline−start→budget) and disabled/hint logic. Keep OSRM notes + over-limit chip unchanged.
- [x] `Components/Shared/Trip/LegConnector.razor` -- swap the edit-mode `<input type=number>` for `<DurationInput Value=ManualMinutes(Leg) ValueChanged=OnManualMinutesChangedAsync Max=TripViewModel.MaxManualLegMinutes AutoFocus OnBlur=StopEditing />`; keep click-to-edit, blank→`ClearManualLegTimeAsync`, and the Manual reset button.
- [x] `Services/UiStrings.cs` -- update `TripTimeLimitAria` to drop "up to 24h".
- [x] tests -- updated `TripStopList_TimeLimitDuration_OverDayLimit_RendersEmpty` → `_RendersHhmm` (now `48:00`); add Finish-by-derives-on-start-change + Limit⇄Finish reflection assertions; add `DurationInput` stepper/parse tests and `TryParseHhmm`/`FormatHhmm` unit tests. Keep existing dwell HH:MM round-trip/reject tests green (component still renders `type="text"`).

**Acceptance Criteria:**
- Given a trip with legs, when the panel renders, then the header is two rows (+ conditional OSRM note), stats sit inline on the left, and Sort/Recompute are bordered buttons that stay disabled while legs compute.
- Given any duration field, when entering a value over 24h (e.g. `48:00`), then it is accepted and round-trips; existing reject-malformed behavior is preserved.
- Given a start time and a Limit, when either Limit or Finish-by is edited, then the other reflects it via the single canonical budget; when the start later changes, Finish-by re-derives while the budget holds.
- Given the per-leg time is edited, when a valid HH:MM is entered, then it persists as the manual leg override (minutes×60) and Manual reset still works.

## Spec Change Log

- **2026-06-16, iter 1 (review patches, no loopback).** Acceptance auditor returned PASS on all ACs. Blind + edge-case hunters converged on one real issue: a Blazor controlled-input gotcha — when `DurationInput`/the derived Finish-by input decline to write (rejected parse, clamp-to-equal, or finish-by guard fails), the diff left the user's raw text in the DOM. Patched centrally with a re-key (`_rev` / `_finishByRev`) so the field always snaps back to the canonical display; bumped `maxlength` 7→8. Added a revert-on-invalid test. Rejected (not caused by this change): autofocus-on-reopen (pre-existing bare `autofocus`), seconds→minutes truncation (unreachable — Manual rows are always minute-multiples), zero/negative-budget semantics (pre-existing guard). KEEP: the single-canonical-budget model, strict `TryParseHhmm`, the shared-component approach.

## Verification

**Commands:**
- `dotnet build LucidCartographer.sln` -- expected: success, no new warnings.
- `dotnet test --filter "FullyQualifiedName~Trip"` -- expected: all Trip component + VM + integration tests pass (per project rule, run after any VM/markup change).

**Manual checks:**
- Header visibly shorter; Limit shows `48:00` for a 2-day budget; ▲▼ = ±5 min, Shift = ±1h, floored at 0; OSRM note still hides under a measured engine.

## Suggested Review Order

**The shared control (entry point)**

- The reusable HH:MM + ▲▼ stepper — read this first to grasp the design.
  [`DurationInput.razor:102`](../../LucidCartographer/Components/Shared/Trip/DurationInput.razor#L102)
- Stepper math: ±Step, Shift ±ShiftStep, clamp [0, Max], no JS (ShiftKey off the event).
  [`DurationInput.razor:128`](../../LucidCartographer/Components/Shared/Trip/DurationInput.razor#L128)
- Re-key fix (review): rejected/clamped entry snaps the field back to canonical display.
  [`DurationInput.razor:97`](../../LucidCartographer/Components/Shared/Trip/DurationInput.razor#L97)

**Conversion edge**

- Uncapped `FormatHhmm` + strict `TryParseHhmm` — the sole minutes⇄HH:MM seam.
  [`TravelTimeFormatting.cs:71`](../../LucidCartographer/Services/Trip/TravelTimeFormatting.cs#L71)

**Linked Limit ⇄ Finish-by (D5)**

- Limit is the canonical budget via a thin pass-through.
  [`TripStopList.razor:590`](../../LucidCartographer/Components/Shared/Trip/TripStopList.razor#L590)
- Finish-by is a derived view = start + budget.
  [`TripStopList.razor:563`](../../LucidCartographer/Components/Shared/Trip/TripStopList.razor#L563)
- Editing Finish-by back-computes the same budget; re-key on reject.
  [`TripStopList.razor:118`](../../LucidCartographer/Components/Shared/Trip/TripStopList.razor#L118)

**Header restructure (#1/#3/#4) & call sites**

- Two-row header: inline stats + Sort/Recompute buttons.
  [`TripStopList.razor:13`](../../LucidCartographer/Components/Shared/Trip/TripStopList.razor#L13)
- Dwell uses the shared control (placeable + unplaceable).
  [`TripStopList.razor:249`](../../LucidCartographer/Components/Shared/Trip/TripStopList.razor#L249)
- Per-leg movement now HH:MM, click-to-edit + reset preserved.
  [`LegConnector.razor:28`](../../LucidCartographer/Components/Shared/Trip/LegConnector.razor#L28)

**Tests (peripheral)**

- Component behavior: parse, steppers, clamp, revert-on-invalid.
  [`DurationInputTests.cs:1`](../../LucidCartographer.Tests/Components/Trip/DurationInputTests.cs#L1)
- Linkage + uncapped-limit render.
  [`TripTimelineRenderTests.cs:447`](../../LucidCartographer.Tests/Components/Trip/TripTimelineRenderTests.cs#L447)
