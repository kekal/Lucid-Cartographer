---
baseline_commit: 19e0388584dcb8018d6ee5220ef25282f4c290e1
---

# Story 2.2: Select Travel Mode and enter a manual Any/Air time

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a trip planner,
I want to choose how I'm travelling and type a known time for a flight leg,
so that the loop's times reflect my mode and my real knowledge.

## Acceptance Criteria

_(Source: epics.md → Epic 2 → Story 2.2; FR-8, AR-10, UX-DR7, UX-DR8, AR-11)_

1. **Travel-Mode selector (UX-DR7).** With Trip View on, a segmented control offers **Any/Air · Drive · Walk · Cycle**, the active segment styled `primary`. It appears on **both** desktop (`TripStopList`) and mobile (`MobileTripPanel`). The choice is **per-trip** (persisted to `PoiCollection.TravelMode`), restored on reopen, and exposes an accessible name / pressed-or-selected state. All labels via `UiStrings`.

2. **Mode change triggers recompute (FR-8).** Selecting a new mode persists it and triggers a travel-time recompute. Because the cache key is `(FromPoiId, ToPoiId, TravelMode)`, the displayed legs immediately switch to the new mode's rows; legs with no row yet under the new mode render "—" + computing and are filled by the background service. (Full deletion/invalidation of the *prior* mode's rows is Story 2.4 — do **not** build a deletion framework here; the prior rows simply become unused.)

3. **Per-mode assumed speed (AR-10).** `MockTravelTimeProvider` derives duration from haversine distance ÷ a **per-mode** assumed speed (Drive/Walk/Cycle each configurable; Any/Air uses the single Any/Air assumed speed). Speeds are configurable via `TravelTimeOptions` (extend the existing `TravelTime` config section), each with a sane default. Units stay canonical (m/s in config, seconds out).

4. **Any/Air ⇒ Placeholder, shown "—" (AR-10, UX-DR5).** Under **Any/Air** mode, a leg with **no manual time** carries **`Fidelity.Placeholder`** and is shown as an em-dash "—" in the user-facing time slot — never a real door-to-door time, never a Placeholder badge. (This refines Story 2.1, where the default-mode Mock returned Estimated.) Drive/Walk/Cycle legs continue to carry **`Fidelity.Estimated`** from the Mock.

5. **Manual time per Any/Air leg (UX-DR8, FR-8).** On an **Any/Air** leg the user can enter a manual travel time (e.g. a flight duration). That leg's value then carries **`Fidelity.Manual`**, overrides the placeholder, and recomputes the trip total. The manual value persists (survives reorder and recompute). The leg's **map line stays dashed + muted** — trust is carried by the Manual badge, not by a solid line (no map-geometry change in this story).

6. **Manual entry is protected from recompute.** The background compute service must **not** overwrite a leg whose existing cache row is `Fidelity.Manual`. A manual entry is only changed/cleared by the user.

7. **Scope & convention gates.** No road geometry, no provider-down fallback (2.3), no general cache-invalidation framework or Estimated→Measured upgrade (2.4), no dwell (2.5), no timeline (2.6). Build passes warnings-as-errors with no group-B analyzer violations; no `ConfigureAwait(false)`; all UI text via `UiStrings`; new decisions tagged `TRIP-TRAVELMODE-01` / `TRIP-MANUAL-01`; **both** surfaces updated; canonical units converted only at the UI edge (minutes↔seconds for the manual entry).

## Tasks / Subtasks

- [x] **Task 1 — Per-mode assumed speed + Any/Air Placeholder in the provider (AC: 3, 4)**
  - [x] Extend `TravelTimeOptions` (`Services/Trip/TravelTimeOptions.cs`) with per-mode speeds — e.g. `DriveSpeedMetersPerSecond`, `WalkSpeedMetersPerSecond`, `CycleSpeedMetersPerSecond`, keeping the existing `AssumedSpeedMetersPerSecond` as the Any/Air speed (or rename consistently and update `appsettings.json` + its `//` doc comment). Sane defaults (e.g. Drive ~13.9, Cycle ~4.2, Walk ~1.4 m/s).
  - [x] `MockTravelTimeProvider.GetLegAsync`: pick the speed by `travelMode`; for `TravelMode.AnyAir` return `Fidelity.Placeholder` (still compute a duration from the Any/Air speed for internal/total use, but the fidelity is Placeholder so the UI shows "—"); for Drive/Walk/Cycle return `Fidelity.Estimated`. Tag `// TRIP-TRAVELMODE-01`. Keep `GeometryPolyline = null`, `Source = "Mock"`.
  - [x] Update the Story 2.1 provider unit tests that asserted Estimated-under-default to reflect AnyAir⇒Placeholder; add per-mode-speed assertions.

- [x] **Task 2 — Persist & change Travel Mode in the ViewModel (AC: 1, 2)**
  - [x] Add a `SetTravelModeAsync(string mode)` to `TripViewModel` mirroring the existing `TripViewEnabled` persistence path (the VM owns `factory` + `SqliteWriteLock`): validate via `TravelMode.IsValid`, write `PoiCollection.TravelMode` under the write lock, re-read projections (legs now read the new mode's cache rows), `TravelTimeTrigger.Signal()` to compute missing new-mode legs, then `Notify()`. Expose the current mode as VM state (e.g. `public string TravelMode { get; private set; }`) for the selector's active segment.
  - [x] Ensure `RefreshProjectionsAsync` already reads `ReadTravelModeAsync` (it does, `TripViewModel.cs:763`) so the projection is mode-correct after a change.
  - [x] No-op guard: selecting the already-active mode does nothing (no write, no recompute) — mirrors the SM-C2 "recomputation stays rare" intent.

- [x] **Task 3 — Manual Any/Air time: persist + protect (AC: 5, 6)**
  - [x] Add a VM method `SetManualLegTimeAsync(int fromPoiId, int toPoiId, int minutes)` (and a clear path): upsert a `RouteSegment` row keyed `(fromPoiId, toPoiId, TravelMode.AnyAir)` with `DurationSeconds = minutes*60`, `DistanceMeters` = haversine (reuse `GeoUtils.HaversineDistance` for display), `Fidelity = Fidelity.Manual`, `Source = "Manual"`, `GeometryPolyline = null`, under the write lock; then refresh + `Notify()`. Convert minutes↔seconds only at this UI edge (AR-11). Tag `// TRIP-MANUAL-01`.
  - [x] In `TravelTimeComputationBackgroundService.LoadPendingLegsAsync` / upsert path: treat a leg whose existing row is `Fidelity.Manual` as **present** (skip — already the behavior, since any existing row is skipped) AND ensure no code path overwrites a Manual row. Add an explicit guard + comment so a future recompute (2.4) can't silently clobber Manual.
  - [x] The leg's `TripLeg.Fidelity` then surfaces `Manual` → `FidelityBadge` already renders the `primary` Manual pill; the map line continues to use the existing dashed+muted treatment (no change — `IsMeasured` stays false for Manual).

- [x] **Task 4 — UI: mode selector + manual entry, both surfaces (AC: 1, 5, 7)**
  - [x] Add a segmented `TravelModeSelector.razor` under `Components/Shared/Trip/` (4 segments, `primary` active, `aria-pressed`/`role=radiogroup` + `UiStrings` labels). Wire its change to `Vm.SetTravelModeAsync`. Render it in `TripStopList.razor` (desktop) and `MobileTripPanel.razor` (mobile) trip headers.
  - [x] Add a manual-time input on each **Any/Air** leg row (a small numeric "minutes" field, `UiStrings`-labelled, `aria-label`led) that calls `Vm.SetManualLegTimeAsync`. Show it only when the active mode is Any/Air. On non-Any/Air modes the input is absent. Keep the leg's existing time/distance/badge slot.
  - [x] All new strings via `UiStrings` (mode names, selector aria, manual-entry label/aria, manual-entry placeholder). No hardcoded text.

- [x] **Task 5 — Tests (AC: all)**
  - [x] **Unit (provider):** per-mode speed selection (Drive/Walk/Cycle distinct durations for same distance); AnyAir ⇒ `Fidelity.Placeholder`; Drive/Walk/Cycle ⇒ `Fidelity.Estimated`.
  - [x] **Unit/VM:** `SetTravelModeAsync` persists `PoiCollection.TravelMode`, signals the trigger, no-ops on same mode; projections read the new mode's rows. `SetManualLegTimeAsync` writes a `(From,To,AnyAir)` row with `Fidelity.Manual`/`Source="Manual"`/correct seconds and updates the total; clear path reverts to Placeholder.
  - [x] **Unit (background):** a leg with an existing `Manual` row is not recomputed/overwritten (assert the row's duration/fidelity unchanged after a compute pass).
  - [x] **Component (bUnit), both surfaces:** selector renders 4 segments with the persisted mode active; switching invokes the VM; the manual input appears only under Any/Air; entering a value shows the Manual badge and updates the total; under Any/Air with no manual, the leg shows "—" and no badge.
  - [x] Full suite green (`!~Integration`) + Trip integration green; no new analyzer warnings.

## Dev Notes

### Scope guardrails
- **In scope:** mode selector (per-trip, persisted, both surfaces), per-mode assumed speed, Any/Air⇒Placeholder rule, manual Any/Air time (persist + protect from recompute), recompute-on-mode-change via the existing trigger.
- **OUT of scope (do NOT build):** road geometry / solid lines (Epic 4); provider-down haversine fallback + failure copy (2.3); a general cache-invalidation framework, explicit "Recompute" button, Estimated→Measured upgrade, or deletion of prior-mode rows (2.4); dwell (2.5); itinerary timeline + lowest-fidelity total qualification (2.6). Mode change here relies on the directional+mode cache key naturally selecting/needing new rows — that's not the 2.4 invalidation framework.

### Built on Story 2.1 (read its code first)
- `MockTravelTimeProvider` (`Services/Trip/MockTravelTimeProvider.cs`) currently returns `Fidelity.Estimated` always and uses one `AssumedSpeedMetersPerSecond`. 2.2 makes speed per-mode and returns `Placeholder` for AnyAir.
- `TravelTimeOptions` (`Services/Trip/TravelTimeOptions.cs`) + `appsettings.json` `"TravelTime"` section (the `//AssumedSpeed…` doc already says "Per-mode speeds arrive in Story 2.2").
- `TripViewModel` (`Components/Shared/Trip/TripViewModel.cs`): reads the persisted mode via `ReadTravelModeAsync` (`:763`, defaults `TravelMode.AnyAir`); reads cache rows filtered by `r.TravelMode == travelMode` (`:796`) — so a mode switch is already cache-key-correct. The VM already persists collection-level Trip state (`TripViewEnabled`) under the write lock — mirror that exact path for `TravelMode`. The progress-driven refresh (`RefreshLegsFromCacheAsync` ~`:837`) re-reads after the background service computes.
- `TravelTimeComputationBackgroundService` (`Services/Trip/TravelTimeComputationBackgroundService.cs`): `LoadPendingLegsAsync` (`:118`) already skips any `(From,To,Mode)` that has a cache row, and `UpsertAsync` (`:206`) updates in place. Add an explicit Manual-row guard so a future recompute can't clobber it.
- `FidelityBadge.razor`: already renders **nothing** for `Placeholder`/null (→ "—") and a `primary` pill for `Manual`. No badge change needed — just feed it the right `Fidelity`.
- `TravelTimeFormatting` + `UiStrings` (`TripDuration*`, `TripDistance*`, `TripFidelity*`, `TripLeg*`): reuse; add only the new selector/manual strings.

### Entities / persistence
- `PoiCollection.TravelMode` (string, EF check-constrained to `TravelMode.All`, default `AnyAir`) already exists (migration `20260611213107_AddTripPlanning`). **No new migration.**
- `RouteSegment` carries `Fidelity` + `Source`; a Manual entry is just a row with `Fidelity = Fidelity.Manual`, `Source = "Manual"`. Directional key `(From,To,AnyAir)`. `Version` concurrency token exists — single-writer (VM under the write lock) so no optimistic-retry needed.
- `TravelMode`/`Fidelity` are string-constant classes (`Data/Entities/TravelMode.cs`, `Fidelity.cs`) with `IsValid`. Validate mode input.

### Architecture & conventions (project-context.md)
- Layering Component→ViewModel→Service→Data; selector/manual-input `.razor` only binds + calls VM methods. No compute in markup.
- No hardcoded UI text; `aria-pressed`/`role=radiogroup` for the selector, `aria-label` on the manual input; both desktop and `Mobile*` paths (UX-DR12). Dark mode first-class (use the token palette).
- Build discipline: warnings-as-errors, no group-B analyzer violations, no `ConfigureAwait(false)`, `CultureInfo` on every format/parse. Tag `TRIP-TRAVELMODE-01` (selector/mode) and `TRIP-MANUAL-01` (manual entry).
- Units (AR-11): config m/s, transport seconds; manual entry is **minutes** at the UI edge → store `*60` seconds. Cache key directional.

### Testing standards
- Unit (provider per-mode + fidelity; VM persist/trigger/manual; background Manual-protection), Component (bUnit selector + manual input on BOTH surfaces via `MobileTestBase`). `InternalsVisibleTo` is set — drive internals directly. Use EF InMemory / temp-SQLite base for persistence tests.

### Previous-story intelligence (2.1)
- 2.1 split `AddTripServices()` (VM-facing, used by the integration test host) vs `AddTripServices(IConfiguration)` (production: provider + options + hosted service). If 2.2 adds new DI, keep that split — anything resolving the Polly `"travel-time"` pipeline or self-firing belongs only in the `IConfiguration` overload, or the integration host boot breaks. New `TravelTimeOptions` fields bind in the `IConfiguration` overload (already there).
- 2.1 review flagged: route all formatted/displayed strings through `UiStrings`; use `CultureInfo.CurrentCulture`; give every value slot a correct `aria-label` (don't reuse a semantically-wrong leftover string).
- The off-circuit refresh mutates VM state from a thread-pool thread (accepted pattern); keep new VM mutations consistent (`Notify()` → `InvokeAsync(StateHasChanged)` for render).

### References
- [Source: _bmad-output/planning-artifacts/epics.md#Story 2.2] — FR-8, AR-10, UX-DR7, UX-DR8
- [Source: _bmad-output/planning-artifacts/epics.md#Additional Requirements] — AR-10 (single Any/Air assumed speed, Manual override), AR-11 (units, directional cache, string enums)
- [Source: _bmad-output/project-context.md] — build/layering/i18n/a11y/testing
- [Source: LucidCartographer/Services/Trip/MockTravelTimeProvider.cs], [TravelTimeOptions.cs], [TravelTimeComputationBackgroundService.cs:118-250], [LucidCartographer/appsettings.json:54-58]
- [Source: LucidCartographer/Components/Shared/Trip/TripViewModel.cs:655-870] (ReadTravelModeAsync :763, cache read :796, progress refresh :837), [FidelityBadge.razor], [TripStopList.razor], [MobileTripPanel.razor]
- [Source: LucidCartographer/Data/Entities/TravelMode.cs], [Fidelity.cs], [RouteSegment.cs]
- [Source: LucidCartographer/Configuration/TripServicesExtensions.cs] (DI split from 2.1)

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (BMAD dev-story; delegated dev subagent + orchestrator verification, AC4-bug fix, and review fixes)

### Debug Log References

- Build (main + tests): 0 warnings / 0 errors.
- Unit/component (`!~Integration`): **608 passed** (incl. new mode/manual + computed-Placeholder honesty tests).
- Trip integration (`Integration&Trip`): **19 passed** (host boot / DI + selection regression guard).

### Completion Notes List

- ✅ AC1 — `TravelModeSelector.razor` (4 segments, `role=radiogroup`/`aria-checked`+`aria-pressed`, `primary` active, `UiStrings` labels) on both surfaces; mode persisted to `PoiCollection.TravelMode`, restored via `ReadTravelModeAsync`.
- ✅ AC2 — `SetTravelModeAsync` persists + `Signal()`s recompute; same/invalid mode is a no-op (no write/recompute).
- ✅ AC3 — per-mode assumed speed via `TravelTimeOptions.SpeedFor(mode)`; bound from the `TravelTime` config section.
- ✅ AC4 — **Any/Air ⇒ `Fidelity.Placeholder`, shown "—"**, Drive/Walk/Cycle ⇒ `Estimated`. (See bug fix below — the provider half shipped from the dev pass, the projection half was added in review.)
- ✅ AC5 — `SetManualLegTimeAsync` writes a `(from,to,AnyAir)` `Manual`/`Source="Manual"` row (minutes×60 s), overrides placeholder, updates total, persists; map line stays dashed+muted (`IsMeasured` false). `ClearManualLegTimeAsync` deletes the row → reverts to Placeholder.
- ✅ AC6 — explicit Manual-row guard in `TravelTimeComputationBackgroundService.UpsertAsync`; test `ProcessOnce_DoesNotOverwrite_ManualRow`.
- ✅ AC7 — clean build, all text via `UiStrings`, `TRIP-TRAVELMODE-01`/`TRIP-MANUAL-01` tags, both surfaces, no scope leak.
- 🐞 **CRITICAL AC4 bug caught in review & fixed:** the provider correctly badged Any/Air as `Placeholder`, but once the background service computed such a leg it stored a real straight-line air estimate, and `MakeLeg`/`TravelTimeFormatting` surfaced that estimate as a real time (e.g. "10m") and summed it into the total — violating "never a real door-to-door time". The passing tests only used *uncomputed* (null) legs, so they missed it. **Fix:** `MakeLeg` nulls the *display* duration for `Placeholder` (keeps distance + the Placeholder fidelity); `RecomputeTotal` now drives `IsAnyLegComputing` off row presence (null fidelity), not null duration, so a computed Placeholder leg shows "—" but is **not** announced as "computing", and the total stays "—". Guarded by new tests `ComputedPlaceholderLeg_HidesDuration_AndIsNotComputing` (VM) and `TripStopList_ComputedPlaceholderLeg_ShowsEmDash_NoBadge_NoRealTime` (render).
- 🐞 **Self-introduced regression caught by the integration suite:** a "min" unit-suffix span (added while resolving the review's unused-constant LOW) widened the leg slot enough that Playwright's click-at-center hit the `stopPropagation` manual-time input, breaking row selection (`TripStopRow_Selection_SetsAriaCurrent_AndReplacesPrior`). **Fix:** dropped the suffix span on both surfaces and removed the unused `TripManualMinutesLabel` constant; kept the `max` cap. Integration green.
- Review MEDIUM fixed: `SetManualLegTimeAsync` now rejects `minutes > MaxManualLegMinutes` (60 days) so `×60` can't overflow `int`; inputs carry `max`.

**Deviations / decisions:**
- `TripViewModel.TravelMode` (string property) shadows the `Data.Entities.TravelMode` type inside the VM; all static uses fully-qualified as `Data.Entities.TravelMode.*` (verified in review — no mis-binding).
- Clear = row delete (simplest revert without touching the 2.4 invalidation framework), then `Signal()` so the Mock refills a Placeholder.
- Manual leg distance = haversine (for display); only the duration is user-supplied.
- Boundary TODOs unchanged: provider-down fallback (2.3); invalidation/Recompute/Estimated→Measured + prior-mode-row deletion (2.4); dwell (2.5); timeline + lowest-fidelity total (2.6).

### File List

**New (3):**
- `LucidCartographer/Components/Shared/Trip/TravelModeSelector.razor`
- `LucidCartographer.Tests/ViewModels/TripViewModelTravelModeTests.cs`
- (review) new tests added to existing files (see Modified)

**Modified (12):**
- `LucidCartographer/Services/Trip/TravelTimeOptions.cs` (per-mode speeds + `SpeedFor`)
- `LucidCartographer/Services/Trip/MockTravelTimeProvider.cs` (per-mode speed; AnyAir⇒Placeholder)
- `LucidCartographer/appsettings.json` (per-mode speeds)
- `LucidCartographer/Components/Shared/Trip/TripViewModel.cs` (`TravelMode` state, `SetTravelModeAsync`, `SetManualLegTimeAsync`/`ClearManualLegTimeAsync` + `MaxManualLegMinutes`; AC4 `MakeLeg`/`RecomputeTotal` honesty fix)
- `LucidCartographer/Services/Trip/TravelTimeComputationBackgroundService.cs` (Manual-row guard)
- `LucidCartographer/Services/UiStrings.cs` (selector + manual strings; removed unused `TripManualMinutesLabel`)
- `LucidCartographer/Components/Shared/Trip/TripStopList.razor` (selector + manual input, desktop)
- `LucidCartographer/Components/Shared/Trip/MobileTripPanel.razor` (selector + manual input, mobile)
- `LucidCartographer.Tests/Services/MockTravelTimeProviderTests.cs` (AnyAir⇒Placeholder + per-mode speed)
- `LucidCartographer.Tests/Services/TravelTimeComputationBackgroundServiceTests.cs` (AnyAir⇒Placeholder fix + Manual-protection test)
- `LucidCartographer.Tests/Components/Trip/TripStopListTests.cs` (2.2 bUnit region, both surfaces)
- `LucidCartographer.Tests/ViewModels/TripViewModelTravelTimeTests.cs` + `LucidCartographer.Tests/Components/Trip/TripTravelTimeRenderTests.cs` (computed-Placeholder honesty tests)

## Change Log

| Date | Change |
|------|--------|
| 2026-06-14 | Story 2.2 implemented: travel-mode segmented selector (per-trip, persisted, both surfaces), per-mode assumed speed, Any/Air⇒Placeholder, per-leg Manual time (persist + protect from recompute). Build clean; 607 unit/component + 19 Trip integration green. Status → review. |
| 2026-06-14 | Adversarial review (fresh context): 2 CRITICAL, 1 MEDIUM, 2 LOW. Fixed the AC4 honesty bug (computed Placeholder leg rendered a real time/total) + strengthened the guard tests; fixed shallow Placeholder test; capped manual minutes (overflow); removed unused constant; fixed a self-introduced selection-regression (layout-shifting suffix span). Rebuild clean; 608 unit/component + 19 Trip integration green. Status → done. |

## Senior Developer Review (AI)

**Outcome:** Approve (done) — after fixing 2 CRITICAL + 1 MEDIUM from the review.
**Reviewer:** Fresh-context adversarial reviewer (separate agent from the implementer).
**Date:** 2026-06-14

**Findings & resolution:**
- [x] [CRITICAL] AC4 violated — a *computed* Any/Air Placeholder leg surfaced its real air estimate as a leg time and in the total. **Fixed** in `MakeLeg`/`RecomputeTotal`; `IsAnyLegComputing` decoupled from null-duration.
- [x] [CRITICAL] The Placeholder render test was too shallow (asserted only badge absence, used null legs). **Fixed:** new VM + render tests assert a computed Placeholder leg shows "—" time, "—" total, no badge, not "computing".
- [x] [MEDIUM] `SetManualLegTimeAsync` `minutes*60` could overflow `int`. **Fixed:** `MaxManualLegMinutes` (60 days) guard + input `max`.
- [x] [LOW] Unused `TripManualMinutesLabel` constant. **Fixed:** removed (and the suffix-span experiment that caused a selection regression was reverted).
- [x] [LOW] (process) story checkboxes/File List blank — populated here.

**AC verdict:** all 7 ACs hold after fixes. No scope leakage into 2.3–2.6.
