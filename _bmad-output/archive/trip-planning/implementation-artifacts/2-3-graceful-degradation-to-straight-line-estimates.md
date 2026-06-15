---
baseline_commit: 1263f4e616867dce96850637aafdd2bff4209dbe
---

# Story 2.3: Graceful degradation to straight-line estimates

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a trip planner whose routing engine is unavailable,
I want legs to fall back to estimates instead of erroring,
so that my loop still works and still tells me the times are approximate.

## Acceptance Criteria

_(Source: epics.md → Epic 2 → Story 2.3; FR-10, NFR2, NFR6, UX-DR10, UX-DR11)_

1. **Fallback, never blank, never error.** When the active provider cannot serve a leg — it throws (unreachable) or signals out-of-coverage / no route — the compute service falls back to the **Estimated (haversine)** computation for that leg rather than failing: the leg gets a real duration/distance, is badged **`Fidelity.Estimated`**, and is **never** left blank and **never** throws out of the compute loop. Other legs in the same pass still compute.

2. **Degraded legs are visibly approximate (UX-DR10/UX-DR11).** A leg served by the *fallback* renders dashed + muted (it is not Measured), and when **any** leg in the trip was served by the fallback the UI shows an honest approximate note — "Couldn't reach the routing engine — showing straight-line estimates." (via `UiStrings`) — on **both** surfaces. A normal Mock-`Estimated` leg (the shipping default, not a degradation) does **not** trigger this note.

3. **Observability (NFR6).** A provider failure is logged, distinguishing the resulting fidelity (Measured vs Estimated/Placeholder/Manual) and naming the leg, so SM-3 can count degraded legs. The loop still orders after a failure.

4. **Any/Air unchanged.** Any/Air keeps its Story 2.2 behavior — it does **not** call a Measured provider and carries **`Fidelity.Placeholder`** (shown "—") absent a Manual entry. It is never "degraded" and never shows the routing-engine-down note. (FR-10's "or the mode is Any/Air" clause is already satisfied: Any/Air never errors because it never calls an out-callable provider.) Do **not** revert 2.2's Any/Air ⇒ Placeholder to Estimated.

5. **Manual & Measured are not overridden.** The fallback only ever fills a leg that would otherwise be blank/failed. It must not overwrite an existing `Manual` row (2.2 guard stays) nor downgrade a `Measured` row.

6. **Scope & convention gates.** Mock is the only provider in Epic 2, so the fallback path is exercised by tests with a stub failing provider — production Mock never fails. No OSRM/road geometry (Epic 4); no cache-invalidation framework / Recompute / Estimated→Measured upgrade (2.4); no dwell (2.5); no timeline (2.6). Build warnings-as-errors, no group-B analyzer violations, no `ConfigureAwait(false)`; all UI text via `UiStrings`; new decisions tagged `TRIP-DEGRADE-01`; both surfaces updated; canonical units.

## Tasks / Subtasks

- [x] **Task 1 — Shared Estimated (haversine) computation (AC: 1, 4)**
  - [x] Factor the haversine→(durationSeconds, distanceMeters) math out of `MockTravelTimeProvider` into a shared internal helper (e.g. `EstimatedTravelTime.Compute(TravelEndpoint from, TravelEndpoint to, string travelMode, TravelTimeOptions options)` in `Services/Trip/`) returning a `TravelLegResult` with `Fidelity.Estimated`, `GeometryPolyline = null`. Have `MockTravelTimeProvider` reuse it for ground modes (keep its Any/Air ⇒ Placeholder branch). Tag `// TRIP-DEGRADE-01`. (DRY — one haversine-estimate code path.)
  - [x] Add a `Source` constant for fallback rows, distinct from the Mock id (e.g. `TravelTimeSource.EstimatedFallback = "EstimatedFallback"`; keep `"Mock"`/`"Manual"`). This is how the VM tells "degraded" from "normally estimated".

- [x] **Task 2 — Fallback in the compute service (AC: 1, 3, 5)**
  - [x] In `TravelTimeComputationBackgroundService.ProcessOnceAsync`, replace the current "log and leave uncomputed" catch with a fallback: on a provider exception (and on a designated no-route signal if/when a provider supports one), compute `EstimatedTravelTime.Compute(...)` and upsert that leg with `Fidelity.Estimated` and `Source = TravelTimeSource.EstimatedFallback`. Never rethrow for a single leg; continue the loop. Tag `// TRIP-DEGRADE-01`.
  - [x] Logging (NFR6): on fallback, `LogWarning` naming the leg `(From→To, Mode)` and the resulting fidelity (Estimated via fallback), distinct from the success-path log. Keep the existing per-leg structure.
  - [x] Preserve the 2.2 guards: the Manual-row guard in `UpsertAsync` stays; never overwrite a `Manual` row, and do not downgrade a `Measured` row (Measured arrives in Epic 4 — add a defensive check so a fallback never replaces a Measured row).

- [x] **Task 3 — VM degraded signal + projection (AC: 2)**
  - [x] In `TripViewModel`, expose a computed `bool IsShowingApproximateEstimates` = any current leg whose backing `RouteSegment.Source == TravelTimeSource.EstimatedFallback`. Read the source alongside the existing cache read in `RefreshProjectionsAsync` (the projection already loads the rows; carry `Source` or a derived bool onto `TripLeg` if needed, e.g. `bool IsFallback`). Keep canonical units; presentation only.
  - [x] Ensure the flag clears when no fallback legs remain (e.g. after a later successful recompute), via the existing refresh path; no polling.

- [x] **Task 4 — UI: approximate note, both surfaces (AC: 2, 6)**
  - [x] When `Vm.IsShowingApproximateEstimates`, render the approximate note (new `UiStrings` constant, honest factual copy per UX-DR11) in `TripStopList.razor` (desktop) and `MobileTripPanel.razor` (mobile), in an `aria-live`/`role=status` region so AT hears it. Use the `warn`/muted token treatment consistent with the palette (not an error/red).
  - [x] Confirm fallback legs already render dashed + muted (they are `Estimated`, not `Measured` → existing line treatment). No map-geometry change.
  - [x] No hardcoded strings.

- [x] **Task 5 — Tests (AC: all)**
  - [x] **Unit (helper):** `EstimatedTravelTime.Compute` matches the Mock's ground-mode output for the same inputs (proves the refactor is behavior-preserving); per-mode speed honored; zero-distance edge.
  - [x] **Unit (background fallback):** inject a stub `ITravelTimeProvider` that throws → `ProcessOnceAsync` upserts an `Estimated` row with `Source = EstimatedFallback` (not blank, no exception escapes); a second leg after a throwing one still computes (loop continues). A stub returning a Measured/Manual existing row is not overwritten/downgraded.
  - [x] **Unit/VM:** with a seeded `EstimatedFallback` row, `IsShowingApproximateEstimates` is true; with only `Mock`/`Manual`/`Placeholder` rows it is false.
  - [x] **Component (bUnit), both surfaces:** degraded flag true → the approximate note renders in an `aria-live` region; healthy trip → no note. Fallback Estimated leg shows the Estimated badge + a real time, dashed+muted.
  - [x] Full unit/component suite green; Trip integration green; no new analyzer warnings.

## Dev Notes

### Scope guardrails
- **In scope:** an exception/no-route → haversine **Estimated** fallback in the compute service (never blank, never throw out of the loop), a `Source`-based "degraded" signal, the honest approximate note on both surfaces, observability logging, and tests via a stub failing provider.
- **OUT of scope:** OSRM / any real out-calling provider, road geometry (Epic 4); cache invalidation framework, explicit "Recompute" action, Estimated→Measured upgrade (2.4); dwell (2.5); itinerary timeline (2.6). Do **not** change Any/Air's Placeholder behavior (2.2). Do **not** add a provider-health/circuit-breaker beyond per-leg try/fallback.

### Built on 2.1 + 2.2 (read first)
- `MockTravelTimeProvider` (`Services/Trip/MockTravelTimeProvider.cs`): ground modes already compute haversine ÷ `TravelTimeOptions.SpeedFor(mode)` → `Estimated`; Any/Air → `Placeholder`. Factor the ground-mode math into the shared `EstimatedTravelTime` helper and have both the Mock and the fallback use it.
- `TravelTimeComputationBackgroundService` (`Services/Trip/TravelTimeComputationBackgroundService.cs`): the per-leg loop in `ProcessOnceAsync` currently `try`s the provider through the Polly `"travel-time"` pipeline and, on exception, **logs and leaves the leg uncomputed** (the TODO there says "Provider-down fallback … is Story 2.3"). This is the exact catch block to replace with the Estimated fallback. `UpsertAsync` has the 2.2 `Manual`-row guard — extend it to also never downgrade a `Measured` row. Writes are gated by `SqliteWriteLock`; per-worker `IDbContextFactory`.
- `TripViewModel` (`Components/Shared/Trip/TripViewModel.cs`): `RefreshProjectionsAsync` reads `RouteSegment` rows filtered by `(From,To,TravelMode)` into `TripLeg` (`MakeLeg`). Add `Source`/`IsFallback` to the projection read and expose `IsShowingApproximateEstimates`. The 2.2 AC4 fix nulls the *display* duration for `Placeholder` only — a fallback `Estimated` leg keeps its real duration. Reuse the existing `StateChanged`/`Notify` + progress-refresh path; no polling.
- `FidelityBadge.razor` already renders the `Estimated` pill (muted `on-surface-variant`) — a fallback leg uses it unchanged. Dashed+muted line treatment for non-Measured legs already exists.
- `TravelLegResult` (`Services/Trip/TravelLegResult.cs`), `RouteSegment` (`Source` column, maxlen 100), `Fidelity`/`TravelMode` constants — reuse. No new migration.

### Reconciling FR-10's "Any/Air" clause with 2.2
FR-10 lists "or the mode is Any/Air" among the fallback triggers. In this codebase Any/Air never calls an out-callable provider (the Mock computes locally and 2.2 badges it Placeholder), so Any/Air already "never errors." Treat the FR-10 clause as **satisfied by 2.2** — keep Any/Air ⇒ Placeholder ("—"); do not route Any/Air through the Estimated-fallback path or re-badge it Estimated.

### Architecture & conventions (project-context.md)
- Layering Component→ViewModel→Service→Data; the note is presentation driven by a VM flag. No compute in markup.
- Build discipline: warnings-as-errors, no group-B analyzer violations, no `ConfigureAwait(false)`, `CultureInfo` on formats. Tag `TRIP-DEGRADE-01`.
- i18n: approximate note + any new text via `UiStrings`. a11y: note in an `aria-live`/`role=status` region; both desktop and `Mobile*`. Use `warn`/muted tokens, never error-red (UX-DR6/DR10 — it's a soft, honest state, not an error).
- Units canonical; the fallback computes seconds/meters like the Mock.

### Testing standards
- Unit (helper parity, background fallback via a throwing stub provider, VM flag), Component (bUnit both surfaces via `MobileTestBase`). `InternalsVisibleTo` set — make `EstimatedTravelTime`/the compute step `internal` and test directly; inject a stub `ITravelTimeProvider` into the background service ctor. EF InMemory / temp-SQLite for upsert tests.

### Previous-story intelligence (2.1 / 2.2)
- Keep the DI split: anything resolving the Polly pipeline / self-firing stays in `AddTripServices(IConfiguration)`; the integration test host calls the parameterless overload and must still boot.
- 2.2 lesson: do not let a UI addition shift the leg-slot layout — a wide element over the row center intercepts the `stopPropagation` manual input and breaks Playwright row-selection. Keep the approximate note in the header/panel area, not inside the clickable stop rows.
- 2.2 lesson: tests must exercise the *computed* path, not just null/seed shortcuts — assert the fallback row's `Source`/`Fidelity` and the rendered note, not only the VM flag.
- Reuse `GeoUtils.HaversineDistance`; never hand-roll haversine.

### References
- [Source: _bmad-output/planning-artifacts/epics.md#Story 2.3] — FR-10, NFR2, UX-DR10, UX-DR11
- [Source: _bmad-output/planning-artifacts/epics.md#Additional Requirements] — AR-2 (Estimated universal fallback), AR-5 (background compute), NFR6 (observability)
- [Source: _bmad-output/project-context.md]
- [Source: LucidCartographer/Services/Trip/MockTravelTimeProvider.cs], [TravelTimeComputationBackgroundService.cs] (catch block / UpsertAsync guard), [TravelTimeOptions.cs], [TravelLegResult.cs]
- [Source: LucidCartographer/Components/Shared/Trip/TripViewModel.cs] (RefreshProjectionsAsync / MakeLeg / RecomputeTotal), [FidelityBadge.razor], [TripStopList.razor], [MobileTripPanel.razor], [Services/UiStrings.cs]
- [Source: LucidCartographer/Data/Entities/RouteSegment.cs], [Fidelity.cs], [TravelMode.cs]

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (BMAD dev-story; delegated dev subagent + orchestrator verification + review fixes)

### Debug Log References

- Build (main + tests): 0 warnings / 0 errors.
- Unit/component (`!~Integration`): **627 passed** (incl. the new direct Manual/Measured no-downgrade guard Theory).
- Trip integration (`Integration&Trip`): **19 passed** (host boot + selection guard).

### Completion Notes List

- ✅ AC1 — provider exception → haversine **Estimated** fallback row (`Source=EstimatedFallback`) in the compute catch; never blank, never rethrows for one leg; a later leg still computes. `OperationCanceledException`-on-cancellation still rethrows.
- ✅ AC2 — `IsShowingApproximateEstimates` (any leg `Source==EstimatedFallback`) drives an honest approximate note in an `aria-live`/`role=status` region on both surfaces (muted tokens, not error-red); a normal `Mock` Estimated leg does not trigger it.
- ✅ AC3 — `LogWarning` names the leg + resulting fidelity on fallback; loop still orders.
- ✅ AC4 — Any/Air **unchanged** (Placeholder "—", never routed through fallback, never re-badged). 2.2 behavior preserved.
- ✅ AC5 — `UpsertAsync` guard never overwrites `Manual` nor downgrades `Measured`.
- ✅ AC6 — clean build, `TRIP-DEGRADE-01` tags, both surfaces, no scope leak; `EstimatedTravelTime` refactor is behavior-preserving (parity test).
- Review (fresh context): **0 CRITICAL / 0 HIGH / 0 MEDIUM / 3 LOW**. Actions:
  - [x] [LOW] The Measured-downgrade guard test was a no-op pass (the seeded row is never re-queued, so the guard branch was never entered). **Fixed:** exposed `UpsertAsync`/`PendingLeg` as `internal` and added `UpsertAsync_NeverDowngrades_ExistingHigherTrustRow` (Theory over Manual + Measured) that drives the guard directly — it would now fail if the guard clause were removed.
  - [ ] [LOW, accepted] Two VM/render degraded tests seed an `EstimatedFallback` row under `TravelMode.AnyAir` — a mode that production never degrades. The flag/note logic is `Source`-based and mode-agnostic, and the real fallback path is faithfully tested under `Drive` (background-service tests), so the assertions remain valid; left as-is to avoid churning shared AnyAir fixtures.
  - [ ] [LOW, accepted] NFR6 fallback log names the leg by integer POI ids (the service layer has no POI names in scope) — greppable and sufficient for SM-3 counting.

**Deviations / decisions:**
- No `warn` token exists in `tailwind.config.js`; the note uses the muted `on-surface-variant`/`surface-container` (desktop) and `--text-2`/`--bg-elev-2` (mobile) — soft/honest, never error-red.
- Fallback triggers on provider **exception** only; a real OSRM "no-route" signal is mapped to the same branch in Epic 4 (noted TODO).
- No new DI registration (fallback is internal compute in the already-registered hosted service); parameterless `AddTripServices` untouched → integration host still boots.
- Boundary TODOs: 2.4 makes the Manual/Measured guards load-bearing (recompute re-queues keys) and should upgrade an `EstimatedFallback` row back to fresh `Mock`/`Measured` once the engine recovers (clearing the note); dwell (2.5); timeline (2.6).

### File List

**New (3):**
- `LucidCartographer/Services/Trip/TravelTimeSource.cs`
- `LucidCartographer/Services/Trip/EstimatedTravelTime.cs`
- `LucidCartographer.Tests/Services/EstimatedTravelTimeTests.cs`

**Modified (9):**
- `LucidCartographer/Services/Trip/MockTravelTimeProvider.cs` (reuse shared estimate; Any/Air Placeholder intact)
- `LucidCartographer/Services/Trip/TravelTimeComputationBackgroundService.cs` (fallback in catch; `UpsertAsync`/`PendingLeg` internal; Manual+Measured guard)
- `LucidCartographer/Components/Shared/Trip/TripProjections.cs` (`TripLeg.IsFallback`)
- `LucidCartographer/Components/Shared/Trip/TripViewModel.cs` (`IsFallback` in both refresh paths; `IsShowingApproximateEstimates`)
- `LucidCartographer/Components/Shared/Trip/TripStopList.razor` + `MobileTripPanel.razor` (approximate note, header area, `role=status`)
- `LucidCartographer/Services/UiStrings.cs` (`TripApproximateEstimatesNote`)
- `LucidCartographer.Tests/Services/TravelTimeComputationBackgroundServiceTests.cs` (fallback + direct guard Theory)
- `LucidCartographer.Tests/ViewModels/TripViewModelTravelTimeTests.cs` + `LucidCartographer.Tests/Components/Trip/TripTravelTimeRenderTests.cs` (degraded-flag + note tests)

## Change Log

| Date | Change |
|------|--------|
| 2026-06-14 | Story 2.3 implemented: provider-exception → haversine Estimated fallback (`Source=EstimatedFallback`, never blank/throw, loop continues), shared `EstimatedTravelTime` helper, `IsShowingApproximateEstimates` + honest approximate note on both surfaces (aria-live, muted), Manual/Measured no-downgrade guard, NFR6 logging. Any/Air Placeholder untouched. Build clean; 625 unit/component + 19 Trip integration green. Status → review. |
| 2026-06-14 | Adversarial review (fresh context): 0 CRITICAL/0 HIGH/0 MEDIUM/3 LOW. Made the Measured/Manual guard genuinely testable (`UpsertAsync` internal + direct Theory); accepted 2 LOW test-fidelity notes. Rebuild clean; 627 unit/component green. Status → done. |

## Senior Developer Review (AI)

**Outcome:** Approve (done) — 0 CRITICAL / 0 HIGH / 0 MEDIUM; 3 LOW (1 fixed, 2 accepted).
**Reviewer:** Fresh-context adversarial reviewer (separate agent from the implementer).
**Date:** 2026-06-14

**Findings & resolution:**
- [x] [LOW] Measured-downgrade guard test was a no-op pass. **Fixed** — direct `UpsertAsync` Theory now drives the guard.
- [ ] [LOW, accepted] Degraded-flag tests seed a fallback row under Any/Air (mode-agnostic flag logic; real path tested under Drive).
- [ ] [LOW, accepted] Fallback log uses integer POI ids (service layer has no names; greppable for SM-3).

**AC verdict:** all 6 ACs hold. AC4 Any/Air Placeholder preserved; production Polly resilience preserved (fallback sits in the catch of the real pipeline `ExecuteAsync`); both VM refresh paths rebuild `IsFallback`; no analyzer/`ConfigureAwait`/hardcoded-string violations; no scope leakage into 2.4–2.6.
