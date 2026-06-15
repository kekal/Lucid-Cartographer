# Story 4.2: Date-aware multi-day arrivals

Status: ready-for-dev

## Story

As a multi-day trip planner, I want arrivals that cross midnight to show their date, so that a trip
reads on its real days instead of wrapping silently.

## Acceptance Criteria

1. **Given** a start date+time is set, **When** arrivals are computed and displayed, **Then** wall-clock arrivals roll across midnight / multiple days, and an arrival on a later calendar day than the start shows its date alongside the time (FR-27, UX-DR12).
2. **Given** date/time formatting, **When** an arrival renders, **Then** it is locale-driven (`CultureInfo.CurrentCulture`) with no hard-coded order (FR-27).
3. **Given** accumulation semantics, **When** arrivals roll across days, **Then** continuous accumulation is unchanged (no overnight "stop for the night" modeling); only formatting changes, at the UI edge (NFR2); **And** the formatting lives in `TravelTimeFormatting`/VM (shared layer), keeping mobile correct (NFR1, NFR5).

## Architecture & Code Context (RD10, FR-27, UX-DR12)

Story 4.1 makes the start a full `DateTime`, so `ItineraryTimeline` already computes
`ArrivalWallClock = start.AddSeconds(offset)` as a real date+time that rolls across midnight/days
(continuous accumulation — already correct; do NOT change the math). The gap is DISPLAY: the arrival
formatter shows only the time-of-day, so a next-day arrival reads ambiguously.

**File:** `LucidCartographer/Services/Trip/TravelTimeFormatting.cs` (`Arrival` and `ArrivalCompact`)
and the two panels' `ArrivalText`/`ArrivalCompactText` bridge helpers (`TripStopList.razor` AND
`MobileTripPanel.razor` — shared-layer correctness reaches both, NFR5).

**Required:**
- The arrival display must show the DATE when the arrival falls on a later calendar day than the trip
  start. Thread the trip start (or its date) so the formatter can decide: pass the trip start
  `DateTime?` into `Arrival`/`ArrivalCompact` (or add an `isLaterDay`/`showDate` flag computed at the
  edge). When `wallClock.Date > tripStart.Value.Date`, render the wall-clock as a **locale-driven
  date+time** (e.g. `CultureInfo.CurrentCulture` short date + short time, via a `UiStrings` pattern —
  no hard-coded `MM/dd` order); otherwise keep the time-only display as today.
  - Prefer computing the per-arrival date in the existing pipeline: e.g. format using
    `CultureInfo.CurrentCulture` so the date component order follows the locale. The `value`/wire
    formats are unaffected; this is DISPLAY only.
- Keep the relative offset (`+2h 15 min`) and the qualifier/`~` honesty markers exactly as today;
  only ADD the date when it's a later day. The compact variant stays compact (date may be the
  short-date; ensure it doesn't overflow — it's now the wide desktop region anyway, and mobile keeps
  its compact behavior).
- No change to `ItineraryTimeline.Compute` math (accumulation unchanged). Formatting only, at the UI
  edge. Both desktop and mobile call the shared formatter → mobile reads correct dates by nature
  (NFR5); update BOTH panels' bridge calls to pass the start/flag.
- New/changed copy via `UiStrings` (a date+time pattern key; no hard-coded order).

## Constraints (NFRs)

- NFR1/NFR2 — formatting at the UI edge in `TravelTimeFormatting`/VM bridge; accumulation math
  unchanged; canonical seconds untouched.
- NFR5 — shared layer; mobile arrival dates correct (update both panels' formatter calls); don't fork.
- NFR6 — locale-driven (`CultureInfo.CurrentCulture`); date pattern via `UiStrings`, no hard-coded order.

## Testing

- `TravelTimeFormattingTests`: a same-day arrival shows time only (unchanged); a later-day arrival
  shows the date alongside the time; locale-driven (assert via `CultureInfo.CurrentCulture` formatting,
  not a hard-coded string order); no trip start → relative offset only (unchanged); qualifier/`~`
  markers preserved.
- bUnit / VM: a multi-day trip (start late in the day + long legs) shows a next-day arrival WITH its
  date on both surfaces.
- Trip integration filter green; mobile trip tests green.

## Build/Test commands

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Trip integration: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`
- Mobile: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Mobile"`

## Dev Notes

Depends on Story 4.1 (full-DateTime start). Shared-layer formatting — reaches mobile by nature; keep
mobile times correct. The finish/return readout (4.5) also benefits from date-aware arrivals.

## Dev Agent Record

(to be filled)

## File List

(to be filled)
