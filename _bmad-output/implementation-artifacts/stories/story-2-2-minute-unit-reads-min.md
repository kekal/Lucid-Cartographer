# Story 2.2: Minute unit reads "min"

Status: done

## Story

As a trip planner, I want durations shown as "min" rather than "m", so that I don't confuse
"22m" (minutes) with "397 m" (distance meters).

## Acceptance Criteria

1. **Given** the duration strings in `UiStrings.TripDuration*`, **When** a duration is formatted, **Then** minutes render as "min" ("22 min", "1h 20 min", "<1 min"); hours stay "h"; distance meters stay "m" (FR-16).
2. **Given** the change is in shared `UiStrings`, **When** either surface renders durations, **Then** desktop and mobile both show "min" (shared layer, NFR5); **And** canonical seconds are unchanged; no literal duration text appears outside `UiStrings` (NFR6).

## Architecture & Code Context (RD5, FR-16)

`UiStrings.TripDuration*` (lines ~195-198): `TripDurationHoursMinutes = "{0}h {1}m"`,
`TripDurationMinutes = "{0}m"`, `TripDurationSubMinute = "<1m"`, `TripDurationZero = "0m"`.
Change the minute unit to "min" (matching the FR-16 examples with a space):
- `"{0}h {1}m"` → `"{0}h {1} min"`  ("1h 20 min")
- `"{0}m"` → `"{0} min"`  ("22 min")
- `"<1m"` → `"<1 min"`
- `"0m"` → `"0 min"`

Hours stay "h". Distance strings (`TripDistance*`, meters "m") are UNCHANGED. Canonical seconds
unchanged. No new logic. Shared layer → both surfaces. Update any tests asserting the old "m"
duration strings to the new "min" forms (faithfully).

## Dev Agent Record

Changed the four `UiStrings.TripDuration*` minute strings to the "min" unit
(`"{0}h {1} min"`, `"{0} min"`, `"<1 min"`, `"0 min"`). Hours stay "h"; distance
`TripDistance*` ("m"/"km") unchanged; canonical seconds unchanged. Shared layer — both
desktop and mobile render "min" by virtue of the shared `TravelTimeFormatting`/`UiStrings`.
Updated all affected test assertions (5 test files) faithfully from "m"→"min".

Review: focused orchestrator self-review (trivial const-string change). Verified no
production code hardcodes the minute unit outside `UiStrings`; distance unchanged;
cross-surface confirmed via mobile suite. 818 fast + 20 Trip integration + 53 mobile green;
build clean (TreatWarningsAsErrors).

## File List

- LucidCartographer/Services/UiStrings.cs (MOD — TripDuration* → "min")
- LucidCartographer.Tests/Services/TravelTimeFormattingTests.cs (MOD)
- LucidCartographer.Tests/ViewModels/TripViewModelTravelTimeTests.cs (MOD)
- LucidCartographer.Tests/Components/Trip/LegConnectorTests.cs (MOD)
- LucidCartographer.Tests/Components/Trip/TripStopListTests.cs (MOD)
- LucidCartographer.Tests/Components/Trip/TripTravelTimeRenderTests.cs (MOD)
