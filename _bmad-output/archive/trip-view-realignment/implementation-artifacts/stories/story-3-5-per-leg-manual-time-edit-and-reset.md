# Story 3.5: Per-leg manual time edit & reset

Status: done

## Dev Agent Record

Generalized manual edit + reset to ANY leg by keying the manual `RouteSegment` write/delete on the
leg's own mode: `UpsertManualSegmentAsync`/`DeleteSegmentAsync` take a `mode` param;
`SetManualLegTimeAsync`/`ClearManualLegTimeAsync` resolve it from the From-stop's
`OutgoingTravelMode` (null→AnyAir) — set and clear use the SAME resolved mode (no orphan asymmetry
within a mode). Setting Manual on a ground leg overwrites its auto row in place; reset deletes the
mode-keyed row + signals compute (ground → re-creates Estimated/Measured; Any/Air → "—").
`LegConnector` time is now click-to-edit on ANY leg (focusable button → inline input;
`stopPropagation`; presentational `_editing` flag, NFR1). TRIP-MANUAL-01 verified already-safe
(compute `have` spans all modes + UpsertAsync Manual/Measured no-downgrade guard; invalidation never
deletes Manual; reset is the only explicit deleter). No ctor dep.

Adversarial review: 0 CRIT / 0 HIGH / 1 MED / 1 LOW → SHIP. **MED (tracked, not fixed here, out of
3.5 scope):** changing a leg's mode (via the 3.4 pill) after a Manual override leaves the old
mode-keyed Manual row orphaned — harmless to display (projection keys by current mode) and
pre-existing, but a stale row / "my manual time vanished" papercut. Promoted to an Epic 3 retro
action item (sweep/migrate Manual rows on mode change). LOW (empty editor pre-fill on a ground leg
with an auto value) accepted as intentional. 880 fast + 20 Trip integration + 54 mobile green.

## File List

- LucidCartographer/Components/Shared/Trip/TripViewModel.cs (MOD — mode-keyed manual write/delete)
- LucidCartographer/Components/Shared/Trip/LegConnector.razor (MOD — click-to-edit any leg)
- LucidCartographer/Services/UiStrings.cs (MOD — TripLegEditTimeAria)
- LucidCartographer.Tests/ViewModels/TripViewModelTravelModeTests.cs (MOD — ground-leg manual tests)
- LucidCartographer.Tests/Components/Trip/LegConnectorTests.cs (MOD — click-to-edit theories)
- LucidCartographer.Tests/Components/Trip/TripStopListTests.cs (MOD — click-to-edit)

## Story

As a trip planner, I want to type a leg's time and later reset it to the automatic value, so that I
can record a flight/train time the app can't estimate and undo it cleanly.

## Acceptance Criteria

1. **Given** any leg (ground or Any/Air), **When** I click the connector's travel time and enter a value, **Then** it sets a Manual override: a `RouteSegment` row at `Fidelity = Manual`, never auto-overwritten and never deleted by invalidation (TRIP-MANUAL-01, FR-25, UX-DR6).
2. **Given** a leg with a Manual override, **When** I use the reset (↺), shown on hover/focus only, **Then** the override is cleared and the leg returns to its auto value: Estimated/Measured for a ground mode (delete the cache row then recompute under `SqliteWriteLock`), or "—"/undefined for Any/Air (FR-25).
3. **Given** the manual/reset write path, **When** it runs, **Then** it stays inside the Trip slice and never downgrades a Manual or Measured row (TRIP-MANUAL-01); results surface via `StateChanged`, not direct mutation (NFR1).

## Architecture & Code Context (RD7, TRIP-MANUAL-01, UX-DR6)

**The gap:** today manual entry is Any/Air-only and the write path HARDCODES the AnyAir cache key.
`TripViewModel.UpsertManualSegmentAsync` / `DeleteSegmentAsync` both filter/write
`r.TravelMode == TravelMode.AnyAir`. Story 3.5 generalizes manual edit + reset to ANY leg, writing
the Manual row at the leg's OWN `(From, To, Mode)` key (TRIP-CACHE-01).

**Required:**
1. **Write path keyed by the leg's mode.** Pass the leg's mode into `UpsertManualSegmentAsync` /
   `DeleteSegmentAsync` (resolve it from the From-stop: `OrderedStops.First(...).OutgoingTravelMode`
   normalized null→AnyAir, which `TripStop` carries since Story 3.2). Upsert/delete the
   `RouteSegment` at `(fromPoiId, toPoiId, legMode)` — NOT hardcoded AnyAir. The Manual row keeps
   `Fidelity = Manual`, `Source = "Manual"`. Update `SetManualLegTimeAsync` /
   `ClearManualLegTimeAsync` accordingly (they already resolve From/To stops for haversine meters —
   add the mode resolution there).
   - Setting Manual on a ground leg with an existing auto (Estimated/Measured) row OVERWRITES that
     `(From,To,ground)` row to Manual (an explicit user override — allowed; the "never downgrade"
     rule is about AUTO compute, not user action).
2. **Reset returns to auto (FR-25).** `ClearManualLegTimeAsync` deletes the Manual row at the leg's
   `(From,To,legMode)` key under `SqliteWriteLock`, refreshes, and signals the background compute
   (it already calls `travelTimeTrigger.Signal()`). For a GROUND leg the compute pass re-creates the
   Estimated/Measured row (it enqueues ground legs lacking a row — Story 3.2). For an Any/Air leg the
   compute skips it (FR-21) so it returns to "—"/undefined. (Existing Signal() already covers both;
   just delete at the correct mode key.)
3. **TRIP-MANUAL-01 protection (verify, don't regress).** The background compute's missing-row
   detection treats an existing `(From,To,Mode)` row as present → it never enqueues/overwrites a
   Manual (or Measured) row. Confirm a Manual row on a GROUND leg is NOT overwritten by the auto
   pass (it's at the ground key, so `have` contains it → skipped). Confirm
   `RouteSegmentInvalidationService` (the recompute/invalidation path) never deletes Manual rows
   (existing rule — don't regress). Reset (delete-then-recompute) is the ONLY deleter of a Manual
   row, and only on explicit user action.
4. **UI — inline edit on the connector time for ANY leg (UX-DR6).** In `LegConnector.razor`, make
   the travel-time element click-to-edit for ANY leg (remove the Any/Air-only gate on the manual
   input). Clicking the time turns it into an inline editable field (e.g. a small number input or
   an inline text field); entering a value calls `Vm.SetManualLegTimeAsync(Leg.FromPoiId,
   Leg.ToPoiId, minutes)`; the reset (↺) — already rendered for `Fidelity == Manual`, hidden at
   rest / shown on hover/focus — calls `Vm.ClearManualLegTimeAsync(...)`. Keep `stopPropagation` so
   editing never selects/reorders the row. Presentational (NFR1) — raises VM commands only. All copy
   via `UiStrings`. (The mode pill from 3.4 stays.)

## Constraints (NFRs)

- NFR1 — connector presentational; manual/reset logic in `TripViewModel` (Trip slice).
- TRIP-MANUAL-01 — Manual rows never auto-overwritten/auto-deleted; reset is the only (explicit)
  deleter; never downgrade Measured by auto compute.
- TRIP-CACHE-01 — manual row keyed by the leg's `(From,To,Mode)`; cache shape unchanged.
- NFR6 — `UiStrings` + tokens. No new ctor dep (NFR10); if added, both overloads.

## Testing

- VM unit: `SetManualLegTimeAsync` on a GROUND leg writes a Manual row at the GROUND `(From,To,mode)`
  key (not AnyAir) and the leg projects Manual fidelity + the entered time; on an Any/Air leg it
  writes at the AnyAir key (existing behavior preserved). `ClearManualLegTimeAsync` on a ground leg
  deletes the ground Manual row and signals compute (→ returns to Estimated); on Any/Air → "—".
- Compute-service test: an auto pass does NOT overwrite a Manual row on a ground leg (it's present
  in `have`); a Measured row is not downgraded.
- bUnit: the connector time is click-to-edit on ANY leg (ground + Any/Air); entering a value raises
  `SetManualLegTimeAsync`; reset shows only for Manual and raises `ClearManualLegTimeAsync`.
- Trip integration filter green; mobile green.

## Build/Test commands

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Trip integration: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`
- Mobile: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Mobile"`

## Dev Notes

Generalizes the existing Any/Air-only manual entry (which hardcoded the AnyAir key) to any leg via
the leg's mode key. 3.6 (MCP) is next; no MCP change here.

## Dev Agent Record

(to be filled by dev)

## File List

(to be filled by dev)
