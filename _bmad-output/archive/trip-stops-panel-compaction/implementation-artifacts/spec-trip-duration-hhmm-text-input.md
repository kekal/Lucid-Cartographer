---
title: 'Trip duration inputs: drop the AM/PM clock control (HH:MM text)'
type: 'bugfix'
created: '2026-06-16'
status: 'done'
baseline_commit: 'e766b3d90bbe014214130495680d33c1408e7738'
context: ['{project-root}/_bmad-output/project-context.md']
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The desktop Trip panel renders three *duration* fields — the trip Time limit and the per-stop Dwell (placeable + unplaceable rows) — with `<input type="time">`. In USA (and any 12-hour) locale the browser draws an AM/PM clock selector. These values are time **budgets/durations** (HH:MM elapsed), not a time of day, so AM/PM is meaningless and misleading.

**Approach:** Change those three inputs from `type="time"` to `type="text"` constrained to an HH:MM duration (placeholder `hh:mm`, `pattern`, `maxlength`). The value stays the invariant `HH:mm` string and continues to convert to/from canonical minutes only at the existing UI edge — no VM, service, data, or canonical-unit changes. The wall-clock Start-time inputs (`datetime-local` on desktop, `type="time"` on mobile) and the mobile numeric-minutes dwell input are untouched.

## Boundaries & Constraints

**Always:** Keep the invariant `HH:mm` wire format (e.g. `90` min ⇄ `01:30`); HH:MM↔minutes conversion stays ONLY at the existing UI-edge helpers (`DurationValue`/`OnDurationChangedAsync`, `DwellMinutes`/`OnDwellMinutesChangedAsync`) — canonical minutes (`TimeBudgetMinutes`, `DwellMinutes`) untouched (NFR2). Parsing stays `TimeOnly.TryParse` with `InvariantCulture`; an unparseable entry is silently ignored (no write); empty/blank clears (null). Preserve the existing `@onclick:stopPropagation`/`@onkeydown:stopPropagation` on the placeable-row dwell input. Keep all strings in `UiStrings`.

**Ask First:** Adding support for entering a bare minute count (e.g. `90`) instead of HH:MM — out of scope unless requested.

**Never:** Do not touch the desktop Start-time `datetime-local`, the finish-by deadline `datetime-local`, or the mobile Start-time `type="time"` (line ~78) — those are real wall-clock times where AM/PM is correct. Do not change the mobile numeric-minutes dwell input. Do not introduce locale-dependent parsing/formatting. Do not change canonical units or add migrations.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Render persisted duration | `TimeBudgetMinutes`/`DwellMinutes` = 90 | text input shows `01:30`, no AM/PM affordance | N/A |
| Render unset | value = null | input empty | N/A |
| Render >24h limit | `TimeBudgetMinutes` > 1440 (deadline path) | Time-limit input empty (unchanged AC4 behavior) | N/A |
| Enter valid HH:MM | user types `02:00` | persists 120 canonical minutes | N/A |
| Enter `00:00` | user types `00:00` | persists 0 minutes | N/A |
| Clear | user blanks the field | persists null (limit/dwell cleared) | N/A |
| Enter unparseable | user types `abc` or bare `90` | no VM write; canonical value unchanged | ignored silently |

</frozen-after-approval>

## Code Map

- `LucidCartographer/Components/Shared/Trip/TripStopList.razor` -- the three affected inputs: Time-limit (~L96), unplaceable-row Dwell (~L244), placeable-row Dwell (~L356); plus the UI-edge helpers `DurationValue`/`OnDurationChangedAsync` (~L560/590) and `DwellMinutes`/`OnDwellMinutesChangedAsync` (~L643/651) and their `type="time"` comments.
- `LucidCartographer/Services/UiStrings.cs` -- `TripTimeLimitPlaceholder`/`TripDwellHhmmPlaceholder` already `hh:mm`; reuse as-is (no new strings expected).
- `LucidCartographer.Tests/Components/Trip/TripStopListTests.cs` -- desktop dwell `type` assertions: `Dwell_Input_Renders_OnEveryRow_UiStringsLabelled` (~L897), `Dwell_Input_Present_OnUnplaceableRow` (~L961).
- `LucidCartographer.Tests/Components/Trip/TripTimelineRenderTests.cs` -- time-limit `type` assertion: `TripStopList_TimeLimitDuration_IsHhmm_AndPersistsMinutes` (~L407).

## Tasks & Acceptance

**Execution:**
- [x] `LucidCartographer/Components/Shared/Trip/TripStopList.razor` -- Changed the three duration inputs from `type="time"` to `type="text"` with `inputmode="numeric"`, `pattern="[0-2]?[0-9]:[0-5][0-9]"`, `maxlength="5"`; updated the surrounding markup comments and the `DurationValue`/`DwellMinutes`/`*ChangedAsync` doc comments to reflect the HH:MM text input. Parsing/formatting and stopPropagation handlers unchanged.
- [x] `LucidCartographer.Tests/Components/Trip/TripStopListTests.cs` -- Updated three desktop `type` assertions (`Dwell_Input_Renders_OnEveryRow_UiStringsLabelled`, `Dwell_Input_Present_OnUnplaceableRow` expectedType, and `TripStopList_RendersOneRowPerStop...` at L80) from `"time"` to `"text"`.
- [x] `LucidCartographer.Tests/Components/Trip/TripTimelineRenderTests.cs` -- Updated `TripStopList_TimeLimitDuration_IsHhmm_AndPersistsMinutes` type assertion + message from `"time"` to `"text"`.

**Acceptance Criteria:**
- Given a desktop Trip panel in a 12-hour locale, when the Time-limit or Dwell field is rendered, then it is an HH:MM text field with no AM/PM selector.
- Given a stop with 90 minutes of dwell, when the row renders, then the dwell field shows `01:30`.
- Given the Time-limit field, when the user enters `02:00`, then `TimeBudgetMinutes` becomes 120; when blanked, it becomes null.
- Given a dwell field, when the user enters `01:30`, then that stop's `DwellMinutes` becomes 90; an unparseable entry leaves it unchanged.
- Given the mobile Trip panel, when rendered, then the Start-time `type="time"` and numeric-minutes dwell inputs are unchanged (no regression).

## Spec Change Log

- **2026-06-16 (step-04 review patch):** Edge-case + blind reviewers found that swapping `type="time"` → `type="text"` removed the browser's structural enforcement, so lenient `TimeOnly.TryParse` would now silently accept `01:30:00` (seconds truncated) and `2:5` (misread as 02:05). Amended the implementation to parse strictly with `TimeOnly.TryParseExact(["H:mm","HH:mm"])` and tightened the `pattern` hour range; added theory tests rejecting non-HH:MM input on both the dwell and time-limit fields. Avoids corrupting canonical `DwellMinutes`/`TimeBudgetMinutes` from malformed-but-parseable text. KEEP: HH:mm invariant wire format, UI-edge-only conversion, mobile untouched.

## Design Notes

The display helpers emit invariant `HH:mm` (`D2`), so the value round-trip is unchanged — only the control's `type` (and thus the browser's day-time AM/PM rendering) changes. Because `type="text"` drops the old control's structural enforcement, parsing is `TimeOnly.TryParseExact` against `["H:mm","HH:mm"]` (not lenient `TryParse`): this restores the rejection of seconds-bearing (`01:30:00`), single-digit-minute (`2:5`), and bare-minute (`90`) inputs that `type="time"` made unreachable — an unparseable entry is ignored (no write). `pattern`/`maxlength` are cosmetic only (Blazor's `@onchange` doesn't honor them), so the C#-edge `TryParseExact` is the real gate. Mobile already uses numeric minutes for dwell and is unaffected; the desktop/mobile dwell-input divergence (`text` vs `number`) is intentional and mirrors the existing `time` vs `number` split.

## Verification

**Commands:**
- `dotnet build LucidCartographer/LucidCartographer.csproj` -- expected: clean (warnings are errors).
- `dotnet test LucidCartographer.Tests --filter "FullyQualifiedName~TripStopListTests|FullyQualifiedName~TripTimelineRenderTests"` -- expected: all green, including the updated `type="text"` assertions and the unchanged round-trip/parse tests.

**Manual checks:**
- Run the app, open a trip with ≥1 leg in an en-US browser; confirm Time-limit and per-stop Dwell fields show a plain `hh:mm` text box with no AM/PM, accept `01:30`, and persist correctly.

## Suggested Review Order

**The control swap (entry point)**

- The fix itself — duration becomes an HH:MM text field, no AM/PM clock.
  [`TripStopList.razor:97`](../../LucidCartographer/Components/Shared/Trip/TripStopList.razor#L97)
- Same swap on the placeable-row dwell (stopPropagation preserved).
  [`TripStopList.razor:360`](../../LucidCartographer/Components/Shared/Trip/TripStopList.razor#L360)
- Same swap on the unplaceable-row dwell.
  [`TripStopList.razor:246`](../../LucidCartographer/Components/Shared/Trip/TripStopList.razor#L246)

**Parsing rigor (the real gate)**

- Strict `TryParseExact` restores the structure `type="time"` used to enforce.
  [`TripStopList.razor:596`](../../LucidCartographer/Components/Shared/Trip/TripStopList.razor#L596)
- Time-limit handler: HH:MM → canonical minutes, unparseable ignored.
  [`TripStopList.razor:598`](../../LucidCartographer/Components/Shared/Trip/TripStopList.razor#L598)
- Dwell handler: same strict path → canonical minutes.
  [`TripStopList.razor:661`](../../LucidCartographer/Components/Shared/Trip/TripStopList.razor#L661)
- Display side unchanged — invariant `HH:mm`, empty for >24h limit.
  [`TripStopList.razor:564`](../../LucidCartographer/Components/Shared/Trip/TripStopList.razor#L564)

**Tests (supporting)**

- Desktop type is now `text`; new theory rejects non-HH:MM dwell input.
  [`TripStopListTests.cs:1030`](../../LucidCartographer.Tests/Components/Trip/TripStopListTests.cs#L1030)
- Time-limit type is `text`; new theory rejects malformed budget input.
  [`TripTimelineRenderTests.cs:468`](../../LucidCartographer.Tests/Components/Trip/TripTimelineRenderTests.cs#L468)
