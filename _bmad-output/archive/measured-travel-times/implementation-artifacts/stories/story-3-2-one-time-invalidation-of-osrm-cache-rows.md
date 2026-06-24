# Story 3.2: One-time invalidation of OSRM cache rows

Status: ready-for-dev

## Story

As a deployment operator migrating to Valhalla,
I want my old OSRM-measured cache rows cleared once on startup,
So that stale, un-reproducible OSRM measurements are recomputed by the active provider instead of being pinned forever by the never-downgrade-Measured guard.

## Acceptance Criteria

1. **Given** existing `RouteSegment` rows with `Source="OSRM"` and Fidelity Measured, and the `[TRIP-MANUAL-01]` guard that would otherwise pin them
   **When** a one-time startup migration runs in `Services/StartupCleanupService.cs`
   **Then** every `RouteSegment` whose `Source` equals the **literal** string `"OSRM"` is deleted under `SqliteWriteLock`, with a code comment noting the literal is intentional because the constant is removed (FR-16, AD-6).
2. `Manual` rows are never touched (FR-16, `[TRIP-MANUAL-01]`).
3. The deleted-row count is logged, and the migration is idempotent/self-retiring (a no-op once the rows are gone) (AD-6).
4. The invalidated legs are subsequently recomputed by the active provider via the existing missing-row trigger.
5. A test asserts OSRM rows are purged, Manual rows survive, and a second run is a no-op.

## Tasks

- [x] Add `PurgeOsrmCacheRowsCoreAsync(db, ct)` static (testable, returns deleted count) deleting `Source == "OSRM"` AND `Fidelity != Manual`.
- [x] Wire it into `StartupCleanupService.StartAsync` under `SqliteWriteLock`, logging the count when > 0.
- [x] Code comment noting the literal `"OSRM"` is intentional (the `TravelTimeSource.Osrm` constant is removed in Story 3.3).
- [x] Tests: OSRM rows purged, Manual survives, Mock/Estimated rows survive, second run is no-op.

## Dev Notes

- Keys on the literal `"OSRM"` so it does not depend on the `TravelTimeSource.Osrm` constant (deleted in 3.3), per AD-6 / epic note.
- Manual rows carry `Source="Manual"` so they never match `"OSRM"`, but the explicit `Fidelity != Manual` belt makes the guarantee structural.
- Recompute happens via the existing missing-row trigger in the background service — no extra wiring needed (AC4).
