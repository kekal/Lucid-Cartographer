# Story 4.4: Dwell HH:MM picker

Status: ready-for-dev

## Story

As a trip planner, I want to enter dwell time as HH:MM, so that setting "1h 30m at the museum" reads
naturally instead of typing raw minutes.

## Acceptance Criteria

1. **Given** the dwell control on a stop row, **When** I enter a dwell, **Then** it is a native HH:MM duration picker writing the canonical `DwellMinutes`; an empty value clears it; no schema change (FR-30, UX-DR4).
2. **Given** the conversion, **When** dwell is read/written, **Then** HH:MM ↔ minutes happens only at the UI edge; canonical `DwellMinutes` is unchanged (NFR2).

## Architecture & Code Context (RD10, FR-30)

**File:** `LucidCartographer/Components/Shared/Trip/TripStopList.razor` (desktop). Each stop row (both
placeable and unplaceable) has a dwell `<input type="number">` in minutes
(`TripDwellPlaceholder`/`TripDwellAria`, `DwellMinutes(row)` value getter ~line 499,
`OnDwellMinutesChangedAsync` ~line 506). Persistence: `Vm.SetDwellMinutesAsync(poiId, int?)`
(canonical minutes, exists — delegates to the ordering service).

**Required (desktop):**
- Replace the dwell number input with a native HH:MM duration picker (`<input type="time">` — dwell
  >24h is implausible, so the 24h cap is fine, per readiness §4).
- Value getter: `DwellMinutes` → format minutes as `HH:mm` (h = m/60, mins = m%60) when set, empty
  when null.
- Change handler: parse `HH:mm` → total minutes → `SetDwellMinutesAsync(poiId, minutes)`; empty →
  `SetDwellMinutesAsync(poiId, null)`. Convert HH:MM ↔ minutes only at the UI edge (NFR2). Keep
  `stopPropagation`, the per-row `aria-label`, and apply on BOTH placeable and unplaceable rows
  (dwell exists on both today).
- Canonical `DwellMinutes` unchanged; no VM/service change; no schema change. Tokens unchanged.

**Mobile:** `MobileTripPanel`'s dwell input is the deferred mirror — leave its number input; do not
break mobile tests.

## Constraints (NFRs)

- NFR2 — `DwellMinutes` stays canonical minutes; HH:MM↔minutes at the UI edge only.
- NFR1 — conversion in the component bridge (as today); no service change.
- NFR6 — tokens/`UiStrings` unchanged (the placeholder may shift from "min" to an HH:MM hint if apt).

## Testing

- bUnit: the dwell control is an HH:MM picker; entering `01:30` persists `SetDwellMinutesAsync(poiId,
  90)`; clearing persists null; `DwellMinutes` round-trips a set value to `HH:mm`. Update existing
  dwell-input tests that asserted `type="number"`/raw minutes faithfully.
- Trip integration filter green; mobile trip tests green (mobile dwell input unchanged).

## Build/Test commands

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Trip integration: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`

## Dev Notes

Smallest Epic 4 story. Canonical `DwellMinutes` and the timeline math are untouched — purely the
input affordance.

## Dev Agent Record

(to be filled)

## File List

(to be filled)
