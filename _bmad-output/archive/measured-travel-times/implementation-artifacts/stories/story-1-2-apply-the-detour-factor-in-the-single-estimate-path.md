# Story 1.2: Apply the detour factor in the single estimate path

Status: done

Adversarial review: 0 CRIT / 0 HIGH / 1 LOW (observational) → SHIP. The detour factor is applied at the single edge (`EstimatedTravelTime.Compute`), so both the Mock default and the background-service fallback (FR-4 / TRIP-DEGRADE-01) inherit it with no second site. Any/Air stays Placeholder with unchanged distance (factor 1.0). Canonical seconds/meters preserved. LOW: `TripViewModel.SetManualLegTimeAsync` still records raw haversine distance for a user-typed leg time — correct: it is NOT the estimate path (duration is user-supplied), so the detour factor is intentionally out of scope there. 983 fast + 20 Trip integration green.

## Story

As a trip planner,
I want ground-leg estimates to account for road winding,
So that the default trip times and distances stop systematically under-estimating real travel.

## Acceptance Criteria

1. **Given** `EstimatedTravelTime.Compute` is the sole estimate edge, reused by both `MockTravelTimeProvider` (default) and the background-service fallback, **When** I apply `adjustedDistance = haversine × DetourFactorFor(mode)` and then `duration = adjustedDistance ÷ SpeedFor(mode)` in that one method, **Then** the default provider reports the **adjusted** distance (meters) and duration (seconds) through the existing seam (FR-1, FR-3).
2. **And** ground legs remain badged **Estimated** and Air/AnyAir legs remain **Placeholder** ("—"), unchanged (FR-3).
3. **And** the provider-failure degrade path produces the identical smart-haversine value with `Source=EstimatedFallback`, preserving `[TRIP-DEGRADE-01]` (FR-4).
4. **And** canonical units stay seconds/meters with conversion only at the edge (NFR-11).
5. **And** unit tests assert the adjusted distance/duration for each ground mode and that Air stays Placeholder.

## Architecture & Code Context

- `LucidCartographer/Services/Trip/EstimatedTravelTime.cs` is the single edge. Multiply the raw haversine meters by `options.DetourFactorFor(travelMode)` to get `adjustedMeters`, then derive `seconds = speed > 0 ? round(adjustedMeters / speed) : 0`. Return `DistanceMeters = adjustedMeters`. Because `DetourFactorFor(AnyAir) == 1.0`, Any/Air distance is unchanged; the Mock re-badges Any/Air to Placeholder as today.
- Because both `MockTravelTimeProvider` and `TravelTimeComputationBackgroundService` fallback route through `EstimatedTravelTime.Compute`, the degrade path automatically yields the identical adjusted value (FR-4 / TRIP-DEGRADE-01) — no second site to change.
- Existing tests in `EstimatedTravelTimeTests.cs` assert raw haversine distance; update them to expect `haversine × factor` faithfully (do not weaken).

## Constraints (NFRs)

- NFR-11 — canonical seconds/meters; factor is dimensionless, applied before the speed division.
- The detour factor must NOT leak into the TSP cost matrix (that is guarded in Story 1.3; `DistanceMatrixService` calls `GeoUtils.HaversineDistance` directly, not this edge).

## Testing

- Unit test: for each ground mode, `DistanceMeters == haversine × DetourFactorFor(mode)` and `DurationSeconds == round(adjustedMeters / SpeedFor(mode))`.
- Unit test: Any/Air via the Mock stays Placeholder and distance is unchanged (factor 1.0).
- Update `Compute_MatchesExpectedHaversineAndPerModeSpeed` and the Mock-equivalence theory to the adjusted expectation.

## Build/Test commands

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast tests: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Trip integration: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`

## Dev Agent Record

`EstimatedTravelTime.Compute` now computes `adjustedMeters = haversine × DetourFactorFor(mode)` then `seconds = round(adjustedMeters ÷ SpeedFor(mode))`, returning `DistanceMeters = adjustedMeters`. Because it is the single estimate edge, the Mock default and the provider-failure fallback both report the adjusted value (FR-4). Any/Air factor is 1.0 so its distance is unchanged and the Mock re-badges it Placeholder as before. Updated `EstimatedTravelTimeTests` (renamed `Compute_AppliesPerModeDetourFactorThenSpeed`; pinned equal factors on the speed-isolation test) and `MockTravelTimeProviderTests` (equal factors on the distinct-durations test) to the adjusted expectation without weakening assertions.

## File List

- LucidCartographer/Services/Trip/EstimatedTravelTime.cs (modified)
- LucidCartographer.Tests/Services/EstimatedTravelTimeTests.cs (modified)
- LucidCartographer.Tests/Services/MockTravelTimeProviderTests.cs (modified)
