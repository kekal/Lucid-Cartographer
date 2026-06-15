# Story 4.1: Date + time start picker

Status: ready-for-dev

## Story

As a multi-day trip planner, I want to set the start as a date and a time, so that a "4–7.06.2026"
trip can anchor to a real day instead of just a time of day.

## Acceptance Criteria

1. **Given** the start control, **When** I set the trip start, **Then** it is a native `datetime-local` (date AND time) writing the existing `PoiCollection.TripStartTime` (`DateTime?`); the `type="time"` + `DateTime.Today` hard-pairing is replaced (FR-26, UX-DR4).
2. **Given** no start is set (empty), **When** the trip renders, **Then** arrivals show relative offsets only (unchanged behaviour) (FR-26).
3. **Given** no schema change is needed, **When** the start is persisted, **Then** it uses the existing `DateTime?` field; conversion/formatting is at the UI edge only (NFR2); the input chrome uses the inherited token styling (UX-DR4).

## Architecture & Code Context (RD10, FR-26)

**File:** `LucidCartographer/Components/Shared/Trip/TripStopList.razor` (desktop). The start input is
currently `<input type="time">` (~line 81) with `StartTimeValue()` formatting `HH:mm` (~line 520) and
`OnStartTimeChangedAsync` parsing `TimeOnly` then pairing `DateTime.Today.Add(...)` (~line 534-545).

**Required (desktop):**
- Change the input to `type="datetime-local"`.
- `StartTimeValue()` → format the active `Vm.TripStartTime` as the `datetime-local` value
  `"yyyy-MM-ddTHH:mm"` (the value attribute format is invariant/ISO regardless of locale — this is the
  control's wire format, NOT display formatting). Empty when `TripStartTime` is null.
- `OnStartTimeChangedAsync` → on empty, `SetTripStartTimeAsync(null)`; otherwise parse the
  `datetime-local` value with `DateTime.TryParse(..., CultureInfo.InvariantCulture,
  DateTimeStyles.None, out var dt)` (the browser emits ISO `yyyy-MM-ddTHH:mm`) and
  `SetTripStartTimeAsync(dt)` — write the FULL date+time, not `DateTime.Today` + time-of-day.
- Keep the existing `UiStrings.TripStartTimeLabel`/`TripStartTimeAria` (label text still "Start"; add
  a new string only if needed). Token styling unchanged. No VM change — `SetTripStartTimeAsync` and
  `TripStartTime` already take `DateTime?`. No schema change.
- The arrivals staying date-AWARE (rolling across midnight / showing dates) is **Story 4.2** — this
  story only changes the START input to date+time and persists the full DateTime. Existing arrival
  display continues to use whatever it does today (time-of-day wall-clock); 4.2 makes it date-aware.

**Mobile:** `MobileTripPanel.razor` still has the `type="time"` start input — leave it for the
deferred mirror phase (its datetime-local upgrade is mobile-control work). Note the cross-surface
nuance: mobile will keep writing `DateTime.Today` + time until the mirror phase; that's an accepted
deferred-state inconsistency (the shared persistence field is the same `DateTime?`). Do not break
mobile tests.

## Constraints (NFRs)

- NFR2 — `TripStartTime` stays `DateTime?`; parse/format only at the UI edge; the `value` wire format
  is ISO (invariant); any DISPLAY formatting (4.2) is locale-driven.
- NFR1 — the parse/format helpers stay the thin component bridge (as today); no logic into services.
- NFR6 — token styling; copy via `UiStrings`.

## Testing

- bUnit: the desktop start input is `type="datetime-local"`; setting a value like `2026-06-04T09:30`
  calls `SetTripStartTimeAsync` with the full `DateTime(2026,6,4,9,30,0)` (date preserved, not
  today's date); clearing → `SetTripStartTimeAsync(null)`; `StartTimeValue()` round-trips a set
  `TripStartTime` to the `yyyy-MM-ddTHH:mm` value. Update any existing desktop start-input test that
  asserted `type="time"` faithfully.
- Trip integration filter green; mobile trip tests green (mobile input unchanged).

## Build/Test commands

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Trip integration: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`

## Dev Notes

Story 4.2 makes arrivals date-aware (multi-day rollover, locale display) on top of this full-DateTime
start. Mobile start-picker upgrade is deferred.

## Dev Agent Record

(to be filled)

## File List

(to be filled)
