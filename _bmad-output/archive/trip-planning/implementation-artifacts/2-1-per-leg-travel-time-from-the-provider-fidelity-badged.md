---
baseline_commit: a81d4dc8f42c920df07bcc8b99fbf92266a70627
---

# Story 2.1: Per-leg travel time from the provider, Fidelity-badged

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a trip planner,
I want each leg to show a travel time and distance with an honest provenance badge,
so that I can read how long each hop takes and how much to trust it.

## Acceptance Criteria

_(Source: epics.md → Epic 2 → Story 2.1; FR-9, AR-2, AR-5, UX-DR5, AR-11)_

1. **Provider contract + Mock default.** A `ITravelTimeProvider.GetLegAsync(fromStop, toStop, travelMode)` returns `(DurationSeconds:int, DistanceMeters:double, Fidelity:string, GeometryPolyline:string?)`. The shipping default is a haversine **Mock** (`MockTravelTimeProvider`), config-selectable, requiring zero routing infrastructure. There is exactly one active provider.

2. **Mock fidelity = Estimated.** When a trip's legs are computed, each placeable leg obtains a duration (seconds) and distance (meters) from the active provider; the Mock yields **`Fidelity.Estimated`** (straight-line distance × a configurable assumed speed), `GeometryPolyline = null`.

3. **Units are canonical (AR-11).** Durations are stored/transported in **seconds**, distances in **meters**; convert to a human-readable form only at the UI edge. No mode-selection UI and no Any/Air Placeholder special-casing in this story (deferred to Story 2.2) — legs compute under the collection's already-persisted `TravelMode` (entity default `TravelMode.AnyAir`).

4. **Per-leg render with Fidelity badge.** When the stop list (desktop + mobile) renders legs, each leg shows its travel time, distance, and a Fidelity badge: **Measured** → `secondary`, **Estimated** → `on-surface-muted`, **Manual** → `primary`. `Placeholder` is internal-only and is **never** shown as a badge — an unmeasured/unentered leg shows its time as an em-dash **"—"** in the user-facing slot.

5. **Trip total.** The trip's total travel time equals the sum of its computed legs' travel times, rendered once for the trip (lowest-fidelity qualification of the total is a Story 2.6 concern — for 2.1 a plain Σ is sufficient, but do not present false precision: if any summed leg is unmeasured/em-dash, show the total as "—").

6. **Off-thread compute via background service (AR-5).** (Re)computation runs off the Blazor circuit thread in a new `TravelTimeComputationBackgroundService`, mirroring `PoiEnrichmentBackgroundService`: per-worker `IDbContextFactory<AppDbContext>`, `SqliteWriteLock` around writes, Polly-wrapped provider calls, woken by a `TravelTimeTrigger` channel. While compute is pending the UI shows a computing state announced via `aria-live`; results land via the ViewModel's `StateChanged` with no manual refresh.

7. **Results written to the cache.** Each computed leg is upserted into the `RouteSegment` cache keyed `(FromPoiId, ToPoiId, TravelMode)` with `DurationSeconds`, `DistanceMeters`, `GeometryPolyline`, `Fidelity`, `Source` (the provider id), `ComputedAt` (UTC). `OrderedLegs` reads the cache for its time/distance/fidelity; a leg with no cache row yet renders "—" + computing state. Cache **invalidation/recompute** triggers (coords/mode/provider change, explicit Recompute) are **out of scope** here — only write-on-compute and read-back. (Invalidation is Story 2.4.)

8. **Build & convention gates.** Build passes with `TreatWarningsAsErrors` and **no group-B analyzer violations** (MA0002/0015/0046/0047/0074, VSTHRD200). No `ConfigureAwait(false)`. All new UI text goes through `UiStrings`. New design decisions carry a searchable `TRIP-*` comment code (e.g. `TRIP-TRAVELTIME-01`). Desktop **and** `Mobile*` render paths both updated.

## Tasks / Subtasks

- [x] **Task 1 — Provider contract + haversine Mock (AC: 1, 2, 3)**
  - [x] Add `ITravelTimeProvider` to `Services/Trip/` (interface-first): `Task<TravelLegResult> GetLegAsync(TripStop from, TripStop to, string travelMode, CancellationToken ct)`. Return a small `readonly record struct TravelLegResult(int DurationSeconds, double DistanceMeters, string Fidelity, string? GeometryPolyline)` in `Services/Trip/`.
  - [x] Implement `MockTravelTimeProvider : ITravelTimeProvider`: haversine distance (reuse the existing `Geolocation` library / NetTopologySuite already referenced — search before adding; do **not** hand-roll haversine if a helper exists) → meters; duration = distance ÷ assumed speed; `Fidelity = Fidelity.Estimated`; `GeometryPolyline = null`; `Source = "Mock"`. Tag `// TRIP-TRAVELTIME-01`.
  - [x] Assumed speed: a single configurable value via an `Options` type (`TravelTimeOptions`, bound from config section `"TravelTime"`), with a sane default (e.g. 50 km/h expressed in m/s). Per-mode speed table is a Story 2.2/AR-10 concern — keep one default speed here; do not build the mode selector.
  - [x] Expose the provider id as a `Source` constant on the provider for the cache `Source` column.

- [x] **Task 2 — Background compute service + trigger (AC: 6, 7)**
  - [x] Add `TravelTimeTrigger` to `Services/Trip/` — copy the `EnrichmentTrigger` channel pattern verbatim (bounded(1), DropWrite, `Signal()` + `WaitAsync(timeout, ct)`).
  - [x] Add `TravelTimeComputationBackgroundService : BackgroundService` in `Services/Trip/`, mirroring `PoiEnrichmentBackgroundService`: inject `IDbContextFactory<AppDbContext>`, `SqliteWriteLock`, `TravelTimeTrigger`, `ITravelTimeProvider`, `ResiliencePipelineProvider<string>`, `IOptions<TravelTimeOptions>`, `ILogger<>`. Loop: `await _trigger.WaitAsync(idlePoll, stoppingToken)` → load the active collection's ordered placeable stops → for each consecutive `(from,to)` pair lacking a fresh cache row, call the provider through the Polly pipeline → upsert `RouteSegment` under the write lock.
  - [x] Register a Polly pipeline `"travel-time"` in `ResilienceExtensions.AddAppResiliencePipelines` (retry + timeout, same shape as `"enrichment"`).
  - [x] DI: add `Services/Trip` registrations — register `ITravelTimeProvider`→`MockTravelTimeProvider`, `TravelTimeTrigger` (singleton), `TravelTimeOptions` (`Configure`), and `AddHostedService<TravelTimeComputationBackgroundService>()` in `Configuration/TripServicesExtensions.cs` (extend the existing `AddTripServices`). Confirm `AddTripServices()` is already wired in `Program.cs` (it is).
  - [x] Surface a tiny progress/computing signal the VM can read (mirror `EnrichmentProgressService` if the VM needs a "legs computing" count; a singleton `TravelTimeProgressService` with a count + `event`/`Signal()` is the established shape). Keep it minimal.

- [x] **Task 3 — Extend leg projection + ViewModel cache read (AC: 4, 5, 6, 7)**
  - [x] Extend `TripLeg` in `Components/Shared/Trip/TripProjections.cs` with nullable computed fields: `int? DurationSeconds`, `double? DistanceMeters`, `string? Fidelity` (null ⇒ not yet computed ⇒ render "—" + computing). Keep existing `IsMeasured` semantics (`IsMeasured = Fidelity == Fidelity.Measured`); update its XML doc (the "always false in Phase 1" comment is now stale for Epic 2).
  - [x] In `TripViewModel.RefreshProjectionsAsync` / `BuildLegs`: after building the straight-leg skeleton, read matching `RouteSegment` rows for the leg pairs under the collection's `TravelMode` and populate the new fields; `MakeLeg` no longer hard-codes `IsMeasured:false`. Add a computed `string? TripTotalTravelTimeDisplay` (or expose total seconds + a nullable flag) per AC5.
  - [x] After enqueuing/triggering a compute (call `TravelTimeTrigger.Signal()` when Trip View turns on or projections rebuild with missing cache rows), subscribe to the progress signal and `Notify()` on `StateChanged` when results arrive — never poll, never block the circuit thread. Respect the VM's existing `CancellationTokenSource`/`IAsyncDisposable` discipline.

- [x] **Task 4 — UI: leg time/distance/Fidelity badge, both surfaces (AC: 4, 5, 8)**
  - [x] Add a small reusable `FidelityBadge.razor` (or extend the leg row) under `Components/Shared/Trip/` rendering the pill: `secondary`/`on-surface-muted`/`primary` per fidelity, `text-xs`, never larger than the time it qualifies; `Placeholder`/null ⇒ no badge, time slot shows "—".
  - [x] Render per-leg time + distance + badge in `TripStopList.razor` (desktop) **and** `MobileTripPanel.razor` (mobile). Format seconds→`Hh Mm` and meters→km/m at the UI edge only (add a small formatting helper; check for an existing one first). Show the trip total per AC5.
  - [x] `aria-live` computing state for legs (reuse the existing announcement region pattern); `aria-label` the time/distance/badge values.
  - [x] Add all new strings to `UiStrings` (e.g. `TripLegTravelTimeAria`, `TripLegDistanceAria`, `TripFidelityEstimated/Measured/Manual`, `TripLegComputingAnnouncement`, `TripTotalTravelTimeLabel`, `TripLegTimeUnknown` = "—"). No hardcoded text.

- [x] **Task 5 — Tests (AC: all)**
  - [x] **Unit:** `MockTravelTimeProvider` — known coords → expected meters (haversine) and seconds (÷ assumed speed), `Fidelity.Estimated`, null geometry. Edge: identical from/to ⇒ 0 distance/0 duration.
  - [x] **Unit:** background service upserts a `RouteSegment` row with correct key/fields and writes under the lock (test the compute method directly via `InternalsVisibleTo`; use EF Core InMemory or the temp-SQLite test base). Re-run is idempotent on an unchanged pair (no duplicate rows).
  - [x] **Unit/VM:** `TripViewModel` populates `TripLeg.DurationSeconds/DistanceMeters/Fidelity` from seeded `RouteSegment` rows; a missing row ⇒ null fields ⇒ "—"; total = Σ; any unmeasured leg ⇒ total "—".
  - [x] **Component (bUnit):** `TripStopList` + `MobileTripPanel` render Estimated badge with `on-surface-muted`, time/distance, and "—" when uncomputed; assert no Placeholder badge ever renders. Cover both desktop and mobile bases (`MobileTestBase`).
  - [x] Full suite green; no new analyzer warnings.

## Dev Notes

### Scope guardrails (prevent scope creep / cross-story collisions)
- **In scope:** provider contract + Mock, background compute service + trigger + Polly pipeline, write to `RouteSegment` cache, read-back into `OrderedLegs`, per-leg time/distance/Fidelity badge + trip total on **both** surfaces.
- **OUT of scope (do NOT implement — later stories will):** travel-mode segmented selector + per-mode speed + Any/Air→Placeholder rule (**2.2**); provider-down haversine fallback + failure logging copy (**2.3**); cache invalidation, "Recompute" action, Estimated→Measured upgrade (**2.4**); dwell (**2.5**); itinerary timeline + lowest-fidelity total qualification + budget overrun (**2.6**); road geometry rendering (**Epic 4**). If you find yourself building any of these, stop.

### Existing code you MUST reuse (do not reinvent)

**RouteSegment entity (already migrated in Epic 1 — do NOT add a migration):**
- `LucidCartographer/Data/Entities/RouteSegment.cs` — key `(FromPoiId, ToPoiId, TravelMode)`; props `DurationSeconds:int`, `DistanceMeters:double`, `GeometryPolyline:string?`, `Fidelity:string`, `Source:string` (maxlen 100), `ComputedAt:DateTime`, `Version:int [ConcurrencyCheck]`.
- Config + check constraints + indexes: `LucidCartographer/Data/AppDbContext.cs:156-194`. Enum check constraints are generated via `EnumCheckSql(column, Enum.All)`. The FK cascade on POI delete already exists.
- The `Version` concurrency token exists; do not bump it manually unless implementing optimistic-concurrency retry (not required for 2.1's single-writer background service).

**Enums (string constants + `.All` + `IsValid`):**
- `Data/Entities/TravelMode.cs` → `AnyAir|Drive|Walk|Cycle`, `TravelMode.All`. Entity default is `AnyAir`.
- `Data/Entities/Fidelity.cs` → `Measured|Estimated|Placeholder|Manual`, `Fidelity.All`. Use `Fidelity.Estimated` for the Mock. Persist as the string constant — never int.
- Precedent: `Data/Entities/PoiCategory.cs` (same shape).

**Background-service pattern to mirror (AR-5):**
- `Services/Enrichment/PoiEnrichmentBackgroundService.cs` — copy its structure: ctor injects `IDbContextFactory<AppDbContext>`, a progress singleton, a trigger, `SqliteWriteLock`, `ResiliencePipelineProvider<string>`, `IOptions<>`, `ILogger<>`. Main loop blocks on `_trigger.WaitAsync(idlePoll, stoppingToken)`. Provider calls go through `pipelineProvider.GetPipeline("travel-time").ExecuteAsync(...)`. Writes are gated:
  ```csharp
  await _writeLock.Gate.WaitAsync(ct);
  try { await db.SaveChangesAsync(ct); }
  finally { _writeLock.Gate.Release(); }
  ```
- `Services/Enrichment/EnrichmentTrigger.cs` — copy verbatim into `TravelTimeTrigger` (bounded(1) `Channel<Unit>`, `DropWrite`, `Signal()`, `WaitAsync`).
- `Services/SqliteWriteLock.cs` — singleton `SemaphoreSlim Gate`. `TripServicesExtensions` already registers it defensively (`TryAddSingletonWriteLock`); reuse, don't re-register a second one.
- Polly registration: `Configuration/ResilienceExtensions.cs:18-52` — add a `"travel-time"` pipeline alongside `"enrichment"` (same retry+timeout shape).

**Trip slice (Epic 1) — where to plug in:**
- `Services/Trip/` currently: `ITripOrderingService.cs`, `TripOrderingService.cs`, `StopPlaceability.cs`. Add the provider, result struct, trigger, options, progress, and background service here (interface-first).
- `Configuration/TripServicesExtensions.cs:6-34` — extend `AddTripServices`; add the hosted service + singletons + options here. `Program.cs:13-31` already calls `.AddTripServices()` and registers hosted services via the same `IServiceCollection` chain (`.AddHostedService<StartupCleanupService>()` precedent).
- `Components/Shared/Trip/TripViewModel.cs` — sealed, primary-ctor DI, `Transient`, `event Action? StateChanged` + `Notify()`, owns `CancellationTokenSource`, `IAsyncDisposable`. Legs are built in `BuildLegs` (`TripViewModel.cs:727`) via `MakeLeg` (`:759-760`), called from `RefreshProjectionsAsync` (`:611-625`). This is exactly where you read the cache and populate the new `TripLeg` fields.
- `Components/Shared/Trip/TripProjections.cs:39-46` — `TripLeg` record to extend. Update the stale `// TRIP-LEG-01 … no leg is Measured in Phase 1` comment (`:27-37`).

**UI surfaces (update BOTH):**
- Desktop: `Components/Shared/Trip/TripStopList.razor` (stop rows ~`:44-99`).
- Mobile: `Components/Shared/Trip/MobileTripPanel.razor`.
- Reusable badge precedent: `Components/Shared/Trip/StopOrderBadge.razor`.
- `Services/UiStrings.cs` (Trip strings grouped ~`:62-134`) — add new constants; format with `string.Format(CultureInfo.CurrentCulture, UiStrings.Key, …)`.

### Architecture & convention constraints (project-context.md)
- **Layering:** Component → ViewModel → Service → Data. The `.razor` `@code` block stays a ~12-line bridge (subscribe/dispose only). All compute logic lives in services; all state in the VM.
- **Units (AR-11):** seconds / meters / minutes; convert at UI edge only. Cache key is **directional** (A→B ≠ B→A) — never collapse pairs.
- **Build discipline:** `TreatWarningsAsErrors`, `Nullable=enable`. No group-B analyzer violations. No `ConfigureAwait(false)` (Blazor circuit sync-context). C# `LangVersion 14` (needs .NET 10 SDK) — already configured; don't touch `Directory.Build.props`.
- **i18n:** every string via `UiStrings`. **Accessibility:** `aria-live` for computing state, `aria-label` on values; both desktop and `Mobile*` paths.
- **Design-decision codes:** tag new decisions `// TRIP-TRAVELTIME-01` (and increment for distinct decisions). Existing Trip codes: `TRIP-LEG-01`, `TRIP-SELECT-01`, `TRIP-PLACE-04`, `TRIP-ORDER-UNPLACE-01`, `TRIP-STARTFINISH-01`, `TRIP-GATE-01`.

### Testing standards (project-context.md)
- Three layers: **Unit** (provider math, background upsert, VM projection), **Component** (bUnit — both desktop + mobile via `MobileTestBase`/`Mobile*Tests`), **Integration** (`IntegrationTestBase` only if an end-to-end pass is warranted — not required for 2.1).
- `InternalsVisibleTo("LucidCartographer.Tests")` is set — test internals directly; mark the background service's compute step `internal` if needed rather than widening public surface.
- Use EF Core InMemory or the temp-SQLite-per-test base for cache-write tests. Assert no duplicate `RouteSegment` rows on idempotent re-compute.

### Project Structure Notes
- New files all land in the existing `Services/Trip/` slice and `Components/Shared/Trip/` UI folder — no new top-level structure. DI stays in `Configuration/TripServicesExtensions.cs`. Matches AR-12 ("`Services/Trip/` vertical slice, interface-first; Trip UI under `Components/Shared/Trip/` with desktop + `MobileTrip*` split").
- No new migration (schema shipped in `20260611213107_AddTripPlanning`). No new NuGet packages expected (Polly, EF, Coravel already present; haversine via existing `Geolocation`/NetTopologySuite — verify before adding anything).

### Previous-story intelligence (Epic 1)
- Epic 1 established: legs are recomputed inside `RefreshProjectionsAsync`/`BuildLegs` and surfaced via `StateChanged`; the host page turns `StateChanged` into the incremental Story-1.3 redraw — reuse that path, do not add a parallel redraw.
- `SqliteWriteLock` is the project-wide write gate; Epic 1's `TripOrderingService` already serializes writes through it — the new background writer must use the same gate to avoid SQLite "database is locked".
- Unplaceable stops are excluded from `OrderedStops`/`OrderedLegs` already (`StopPlaceability`); leg compute therefore only ever sees placeable consecutive pairs — no extra null-coord guarding needed beyond what `BuildLegs` already does.
- Known env note: one pre-existing flaky integration test (`Union_ShowsAllUniquePois` / a scraper progress test) passes in isolation — not a regression; don't chase it.

### References
- [Source: _bmad-output/planning-artifacts/epics.md#Story 2.1] — AC, FR-9, AR-2, AR-5, UX-DR5
- [Source: _bmad-output/planning-artifacts/epics.md#Additional Requirements] — AR-2 (provider contract+Mock), AR-4 (cache — read its shape only; invalidation is 2.4), AR-5 (background compute), AR-11 (units, directional cache, string enums)
- [Source: _bmad-output/project-context.md] — build/layering/testing/i18n/a11y rules
- [Source: LucidCartographer/Data/Entities/RouteSegment.cs], [AppDbContext.cs:156-194], [Fidelity.cs], [TravelMode.cs]
- [Source: LucidCartographer/Services/Enrichment/PoiEnrichmentBackgroundService.cs], [EnrichmentTrigger.cs], [Services/SqliteWriteLock.cs], [Configuration/ResilienceExtensions.cs:18-52], [Configuration/EnrichmentPipelineExtensions.cs]
- [Source: LucidCartographer/Configuration/TripServicesExtensions.cs], [Program.cs:13-31]
- [Source: LucidCartographer/Components/Shared/Trip/TripViewModel.cs:611-760], [TripProjections.cs:39-46], [TripStopList.razor], [MobileTripPanel.razor], [StopOrderBadge.razor], [Services/UiStrings.cs]

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (BMAD dev-story; implementation via delegated dev subagent, verification + DI-regression fix by orchestrator)

### Debug Log References

- Build (main + tests): `dotnet build` → succeeded, 0 warnings / 0 errors (TreatWarningsAsErrors clean; no group-B analyzer violations; no `ConfigureAwait(false)`).
- Unit/component suite (`FullyQualifiedName!~Integration`): **589 passed, 0 failed, 0 skipped** (incl. 19 new travel-time tests; stable across repeat runs).
- Trip integration suite (`Integration&Trip`): **19 passed** after the DI fix below (initially 19 *failed* on host boot — see Completion Notes).

### Completion Notes List

- ✅ AC1 — `ITravelTimeProvider.GetLegAsync` → `TravelLegResult`; single registered active provider (`MockTravelTimeProvider`). Provider param uses a layer-local `TravelEndpoint(PoiId,Lat,Lon)` instead of `TripStop` (layering: Services must not depend up on Components — see Deviations).
- ✅ AC2 — Mock = haversine (`GeoUtils.HaversineDistance`) ÷ assumed speed → `Fidelity.Estimated`, null geometry.
- ✅ AC3 — seconds/meters end-to-end; converted only at the UI edge (`TravelTimeFormatting`); legs compute under the collection's persisted `TravelMode`.
- ✅ AC4 — both surfaces render time/distance + `FidelityBadge` (Estimated→`on-surface-variant`, Measured→`secondary`, Manual→`primary`); Placeholder/null ⇒ no badge, "—". Asserted Placeholder badge **never** renders.
- ✅ AC5 — `TotalTravelTimeSeconds` = Σ legs; any uncomputed leg ⇒ total "—".
- ✅ AC6 — `TravelTimeComputationBackgroundService` mirrors enrichment (factory, write-lock, Polly `"travel-time"`, trigger); computing state via `aria-live`; results land via `StateChanged` (progress subscription, no polling).
- ✅ AC7 — upsert into `RouteSegment` keyed `(From,To,TravelMode)`; idempotent (no dup rows); VM reads back; missing row ⇒ "—". No new migration.
- ✅ AC8 — clean build, all text via `UiStrings`, `TRIP-TRAVELTIME-01` tags, both desktop + mobile updated.
- 🐞 **Regression caught & fixed in verification (not by the initial unit run):** bundling `AddHostedService<TravelTimeComputationBackgroundService>()` + the Mock provider into the *parameterless* `AddTripServices()` broke the integration host boot — `IntegrationTestBase` composes services by hand and never registers the Polly `"travel-time"` pipeline, so the hosted service threw `KeyNotFoundException: Unable to find a resilience pipeline 'travel-time'` at `StartAsync`, and would also have self-fired a background loop the test base deliberately avoids (cf. the excluded dedup loop). **Fix:** split `TripServicesExtensions` — the parameterless overload registers only VM-facing services (`ITripOrderingService`, `TravelTimeTrigger`, `TravelTimeProgressService`, shared `SqliteWriteLock`); the `IConfiguration` overload (called only by `Program.cs`) adds the provider, options, and hosted service. `TripViewModel` needs the trigger+progress but not the provider, so the VM renders in tests without the compute loop.

**Deviations / decisions (carry into code review):**
- `TravelEndpoint` record introduced for the provider signature (layering) instead of the `TripStop` named in the story.
- Estimated badge mapped to `on-surface-variant` (the codebase's muted token); the story's `on-surface-muted` token does not exist in `tailwind.config.js`.
- Background service computes for **all** `TripViewEnabled` collections (no single "active collection" at the service layer); a leg is computed iff it has no cache row — invalidation/recompute deferred to Story 2.4.
- Per-leg slot shows the leg **departing** that stop (covers the roundtrip closing leg). Story 1.3 timeline placeholder superseded; dwell placeholder stays inert (Story 2.5).
- Progress subscription uses `Skip(1)` to drop the BehaviorSubject's replayed initial value so it can't race the initial projection refresh.
- TODOs left at story boundaries: provider-down fallback + failure copy (2.3); mode selector / per-mode speed / Any-Air→Placeholder (2.2); cache invalidation / Recompute / Estimated→Measured upgrade (2.4); dwell (2.5); itinerary timeline + lowest-fidelity total (2.6).

### File List

**New (13):**
- `LucidCartographer/Services/Trip/ITravelTimeProvider.cs`
- `LucidCartographer/Services/Trip/TravelLegResult.cs`
- `LucidCartographer/Services/Trip/MockTravelTimeProvider.cs`
- `LucidCartographer/Services/Trip/TravelTimeOptions.cs`
- `LucidCartographer/Services/Trip/TravelTimeTrigger.cs`
- `LucidCartographer/Services/Trip/TravelTimeProgressService.cs`
- `LucidCartographer/Services/Trip/TravelTimeComputationBackgroundService.cs`
- `LucidCartographer/Services/Trip/TravelTimeFormatting.cs`
- `LucidCartographer/Components/Shared/Trip/FidelityBadge.razor`
- `LucidCartographer.Tests/Services/MockTravelTimeProviderTests.cs`
- `LucidCartographer.Tests/Services/TravelTimeComputationBackgroundServiceTests.cs`
- `LucidCartographer.Tests/ViewModels/TripViewModelTravelTimeTests.cs`
- `LucidCartographer.Tests/Components/Trip/TripTravelTimeRenderTests.cs`

**Modified (13):**
- `LucidCartographer/Components/Shared/Trip/TripProjections.cs`
- `LucidCartographer/Components/Shared/Trip/TripViewModel.cs`
- `LucidCartographer/Components/Shared/Trip/TripStopList.razor`
- `LucidCartographer/Components/Shared/Trip/MobileTripPanel.razor`
- `LucidCartographer/Configuration/TripServicesExtensions.cs`  *(split into parameterless + IConfiguration overloads — DI regression fix)*
- `LucidCartographer/Configuration/ResilienceExtensions.cs`
- `LucidCartographer/Program.cs`
- `LucidCartographer/Services/UiStrings.cs`
- `LucidCartographer/appsettings.json`
- `LucidCartographer.Tests/Components/Trip/TripStopListTests.cs`
- `LucidCartographer.Tests/Components/Trip/TripToggleTests.cs`
- `LucidCartographer.Tests/Services/TripPlaceableTests.cs`
- `LucidCartographer.Tests/ViewModels/TripViewModelTests.cs`

## Change Log

| Date | Change |
|------|--------|
| 2026-06-14 | Story 2.1 implemented: `ITravelTimeProvider` + haversine `MockTravelTimeProvider`, off-circuit `TravelTimeComputationBackgroundService` (`travel-time` Polly pipeline + `TravelTimeTrigger`/`TravelTimeProgressService`), `RouteSegment` cache write/read-back, per-leg time/distance + `FidelityBadge` on desktop + mobile, trip total. Fixed an integration-host DI regression by splitting `AddTripServices`. Build clean; 589 unit/component + 19 Trip integration green. Status → review. |
| 2026-06-14 | Adversarial code review (fresh-context reviewer): 0 CRITICAL, 0 HIGH, 1 MEDIUM, 3 LOW; all 8 ACs verified IMPLEMENTED, no false task claims, no scope leakage. Auto-fixed MEDIUM (duration/distance format literals in `TravelTimeFormatting` now routed through new `UiStrings.TripDuration*`/`TripDistance*` constants — AC8/i18n) and one LOW (empty-leg slot mis-labelled `TripTimelineAria` → new `TripLegNoTravelTimeAria` on both surfaces). Two LOWs accepted as documented (off-circuit reference-assignment race + read-modify-write outside the gate both mirror the established enrichment single-writer pattern). Rebuild clean; 589 unit/component green. Status → done. |

## Senior Developer Review (AI)

**Outcome:** Approve (done) — 0 CRITICAL / 0 HIGH after fixes.
**Reviewer:** Fresh-context adversarial reviewer (separate agent from the implementer).
**Date:** 2026-06-14

**Findings & resolution:**
- [x] [MEDIUM] `TravelTimeFormatting.cs` — hardcoded duration/distance format literals bypassed `UiStrings` (AC8/i18n). **Fixed:** added `TripDurationHoursMinutes/Minutes/SubMinute/Zero` + `TripDistanceKilometers/Meters` and routed the formatter through them.
- [x] [LOW] `TripStopList.razor` / `MobileTripPanel.razor` — empty-leg ("no departing leg") slot used the leftover `TripTimelineAria` ("Arrival time…"). **Fixed:** added `TripLegNoTravelTimeAria` and applied on both surfaces.
- [ ] [LOW, accepted] `TripViewModel.RefreshLegsFromCacheAsync` mutates VM state from a thread-pool thread (rendering marshalled via `InvokeAsync`); a benign reference-assignment race that mirrors the existing enrichment-progress pattern. Accepted as consistent with the codebase.
- [ ] [LOW, accepted] `TravelTimeComputationBackgroundService.UpsertAsync` does its read-modify-write outside the `SqliteWriteLock` (only `SaveChangesAsync` is gated); correct for the single-writer hosted loop. Revisit if Story 2.4's concurrent recompute introduces a second writer.

**AC verdict:** all 8 ACs IMPLEMENTED. **Tests:** real assertions incl. haversine math, zero-distance edge, idempotency (row-count), VM cache read, total-em-dash, "Placeholder badge never renders", desktop + mobile render paths.
