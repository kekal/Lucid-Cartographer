# Story 1.1: Configurable per-mode detour factors

Status: done

Adversarial review: 0 CRIT / 0 HIGH / 0 MED → SHIP. Options-only change; accessor mirrors SpeedFor, Any/Air returns 1.0; defaults exactly 1.3/1.2/1.15; appsettings keys documented as [ASSUMPTION]. Clean build under TreatWarningsAsErrors. 983 fast tests green.

## Story

As a deployment operator,
I want per-mode detour/winding factors I can configure (with sane shipped defaults),
So that I can tune how realistically the default estimate reflects real road distance for my region without touching code.

## Acceptance Criteria

1. **Given** the `TravelTime` config section already binds per-mode speeds into `TravelTimeOptions`, **When** I add per-mode detour factors (Drive, Cycle, Walk) to `TravelTimeOptions` and to `appsettings.json`, **Then** each factor is bindable from the existing `TravelTime` section (e.g. `TravelTime:DriveDetourFactor`).
2. **And** documented defaults ship as `[ASSUMPTION]` values Drive ×1.3, Cycle ×1.2, Walk ×1.15 (FR-2).
3. **And** a `DetourFactorFor(mode)` accessor mirrors the existing `SpeedFor(mode)` shape and returns the configured (or default) factor for any ground mode (Any/Air falls through to ×1.0, no detour).
4. **And** the build is clean under `TreatWarningsAsErrors` with no group-B analyzer violations (NFR-12).

## Architecture & Code Context

- `LucidCartographer/Services/Trip/TravelTimeOptions.cs` already exposes per-mode speeds + `SpeedFor(string)`. Add `DriveDetourFactor`, `CycleDetourFactor`, `WalkDetourFactor` doubles with the shipped defaults, plus `DetourFactorFor(string travelMode)` mirroring the `SpeedFor` switch. Any/Air returns 1.0 (no winding applied — Air is a straight line by nature).
- `LucidCartographer/appsettings.json` `TravelTime` section: add `DriveDetourFactor`/`CycleDetourFactor`/`WalkDetourFactor` keys with `//`-prefixed doc comments matching the existing speed-key convention, documenting them as `[ASSUMPTION]` defaults.
- This story is options-only — it does NOT apply the factor (that is Story 1.2). The accessor must exist and be tested in isolation.

## Constraints (NFRs)

- NFR-11 — canonical units unchanged; the factor is a dimensionless multiplier.
- NFR-12 — clean build under `TreatWarningsAsErrors`, no group-B analyzer violations.

## Testing

- Unit test that `DetourFactorFor` returns the configured value per ground mode and the default when unset, and 1.0 for Any/Air.
- Unit test that the shipped defaults are exactly 1.3 / 1.2 / 1.15.

## Build/Test commands

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast tests: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`

## Dev Agent Record

Added `DriveDetourFactor`/`CycleDetourFactor`/`WalkDetourFactor` (defaults 1.3/1.2/1.15) and `DetourFactorFor(travelMode)` (mirrors `SpeedFor`; Any/Air → 1.0) to `TravelTimeOptions`. Added `//`-documented `[ASSUMPTION]` keys to the `TravelTime` section of `appsettings.json`. New `TravelTimeOptionsTests` cover defaults, configured values, and the Any/Air 1.0 fall-through. Build clean (0 warnings, TreatWarningsAsErrors).

## File List

- LucidCartographer/Services/Trip/TravelTimeOptions.cs (modified)
- LucidCartographer/appsettings.json (modified)
- LucidCartographer.Tests/Services/TravelTimeOptionsTests.cs (added)
