# Story 3.1: Prominent warn-and-fallback for retired/unknown provider ids

Status: ready-for-dev

## Story

As a deployment operator upgrading from OSRM,
I want a stale `TravelTime:Provider=Osrm` (or any unknown value) to keep my app booting while loudly telling me my deployment downgraded to estimates,
So that a forgotten config never bricks the deployment but also never silently demotes me from Measured to Estimated unnoticed.

## Acceptance Criteria

1. **Given** the DI selection now recognizes only `Valhalla` (and the implicit default), per Epic 2 Story 2.4
   **When** `TravelTime:Provider` is set to a retired/unknown value such as `Osrm`
   **Then** the app falls back to the smart-haversine default and **does not** fail to boot (FR-15, AD-7 — warn+fallback, not fail-fast).
2. A prominent high-level startup warning is emitted naming the offending value and stating that routing is now Estimated, not Measured (FR-15).
3. The migration/release note calls out the breaking change and the warn+fallback behavior (FR-15, PRD §8).
4. A unit/integration test asserts the warning is logged and the active provider is the default for an unknown id.

## Tasks

- [x] Add a testable provider-selection classifier in `TripServicesExtensions` that distinguishes: default (empty/Mock), Valhalla, and retired/unknown.
- [x] Emit a prominent startup `LogWarning` naming the offending value when retired/unknown, then register the Mock default.
- [x] Keep `Valhalla` and the implicit default behavior unchanged (no regression to Story 2.4).
- [x] Add a release/migration note in `docs/valhalla.md` (or the migration callout) for the breaking change.
- [x] Tests: unknown id → warning logged + active provider is Mock; Valhalla id → no warning; default → no warning.

## Dev Notes

- Selection happens at DI-registration time, before the app's `ILogger` is built. Use a bootstrap logger factory for the warning, and expose the classification as a pure static so it is unit-testable without standing up the host.
- `[TRIP-MANUAL-01]` / degrade paths unaffected — this is provider-selection only.
