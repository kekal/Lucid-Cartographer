---
baseline_commit: da4b8882dc712d36259447726126bcea11e1c153
---

# Story 2.3: Capability-gated recompute trigger + degrade

Status: done

## Story

As a trip planner,
I want estimated legs to be upgraded to measured values once a measured provider becomes available, and any failing measured leg to fall back cleanly,
So that the trip view converges on the best available fidelity without ever failing the batch or downgrading good data.

## Acceptance Criteria

1. **Given** `TravelTimeComputationBackgroundService.LoadPendingLegsAsync` today enqueues a directional leg **iff no cache row exists** for its `(FromPoiId, ToPoiId, TravelMode)` key (the `have.Contains(key)` membership check against the `RouteSegments` key set), **When** I broaden the pending-leg predicate to **"no row exists OR the row is upgrade-eligible"**, where **upgrade-eligible = `Fidelity ∈ {Estimated, Placeholder}` AND `Source ∈ {Mock, EstimatedFallback}`** (AD-2), **Then** a row matching that upgrade-eligible shape is re-enqueued and recomputed, while a row that fails it is left as-is.
2. **And** the broadened arm is included **only when** the active provider's `ITravelTimeProvider.ProducesMeasuredFidelity` is `true` (the seam bool from Story 2.1; Mock=`false`, Valhalla=`true`), so a Mock deployment never re-churns its own Estimated rows into an infinite recompute loop (AD-2). When `ProducesMeasuredFidelity` is `false`, the predicate collapses to the existing "no row exists" behavior exactly.
3. **And** the upsert guard in `UpsertAsync` still returns early on `existing.Fidelity is Fidelity.Manual or Fidelity.Measured`, so even if a protected row were somehow re-read it is never downgraded (`[TRIP-MANUAL-01]`, NFR-10). This guard is unchanged by this story — it is the defensive belt behind the broadened read.
4. **And** any provider failure for a leg (including `ValhallaRouteUnavailableException`) degrades **that leg** to the smart-haversine estimate via `EstimatedTravelTime.Compute`, stamped `Source = TravelTimeSource.EstimatedFallback` with `Fidelity.Estimated`, **one leg at a time**, never throwing out of `ProcessOnceAsync` (FR-8, `[TRIP-DEGRADE-01]`). A genuine caller-token cancellation (`OperationCanceledException` when `ct.IsCancellationRequested`) still re-throws and aborts the pass — that path is unchanged.
5. **And** the leg **shape** — consecutive `k→k+1` pairs plus the closing leg back to the first stop on Roundtrip (no distinct Finish) — is **unchanged**, keeping the three-site leg-projection mirror aligned: `TravelTimeComputationBackgroundService.DirectionalPairs`, `TripViewModel.BuildLegs`, and the MCP `GetTrip` projection must continue to produce the identical leg set (AD-2 mirror check). This story touches only the *pending/eligibility* decision, not which legs exist.
6. **And** unit tests cover: an upgrade-eligible row is recomputed when the provider is measured-capable; the same row is left untouched when the provider is Mock (`ProducesMeasuredFidelity==false`); `Manual` and `Measured` rows are never re-enqueued regardless of provider capability; and a failing leg degrades to `EstimatedFallback` without aborting the batch.
7. **And** the solution compiles clean under `TreatWarningsAsErrors` with no group-B analyzer violations (MA0002/MA0015/MA0046/MA0047/MA0074/VSTHRD200) and both the fast suite and the Trip integration filter stay green (NFR-12, NFR-13).

## Architecture & Code Context

This is the **recompute trigger** for Epic 2 — the piece that makes a freshly-reachable measured provider (Valhalla, Story 2.2) actually *upgrade* the Estimated/fallback rows a deployment accumulated while measured routing was unavailable (e.g. during the Story 2.5 first-boot tile-build window, FR-13a). Without it, once an Estimated row lands in `RouteSegments` the leg is "not pending" forever and never gets the measured value even after Valhalla comes up.

The change is **surgical and single-file** in production: it broadens the pending-leg predicate inside `LoadPendingLegsAsync` in `LucidCartographer/Services/Trip/TravelTimeComputationBackgroundService.cs`. The capability gate (`provider.ProducesMeasuredFidelity`) and the upsert no-downgrade guard (`UpsertAsync`) already exist — this story *consumes* the seam Story 2.1 added and leans on the guard Story 2.2 hardened. **Do not touch the leg-shape projection** (`DirectionalPairs`) or the upsert guard logic; do not wire DI or add config (that is Story 2.4).

### Current state — what `LoadPendingLegsAsync` does today (READ THIS FIRST)

In `TravelTimeComputationBackgroundService.cs`, `LoadPendingLegsAsync` (around lines 109–173) builds the pending set as follows:

1. Loads the Trip-View-enabled collections (`Id`, `StartPoiId`, `FinishPoiId`).
2. Loads **every** `RouteSegment` projected to `{ FromPoiId, ToPoiId, TravelMode }` and builds a `HashSet` named `have` of `(FromPoiId, ToPoiId, TravelMode)` keys:
   ```csharp
   var existing = await db.RouteSegments
       .AsNoTracking()
       .Select(r => new { r.FromPoiId, r.ToPoiId, r.TravelMode })
       .ToListAsync(ct);
   var have = existing
       .Select(r => (r.FromPoiId, r.ToPoiId, r.TravelMode))
       .ToHashSet();
   ```
3. For each collection, projects ordered placeable stops into `PendingStop`s carrying each From-stop's `OutgoingTravelMode` (null normalized to `TravelMode.AnyAir`).
4. Walks `DirectionalPairs(stops, c.FinishPoiId)`, skips non-ground modes (`IsGroundMode`), and for each ground leg computes `var key = (from.PoiId, to.PoiId, mode);` then enqueues it **only if it is not already cached and not already seen this pass**:
   ```csharp
   var key = (from.PoiId, to.PoiId, mode);
   if (have.Contains(key) || !seen.Add(key))
   {
       continue;
   }
   pending.Add(new PendingLeg(from, to, mode));
   ```

The `have.Contains(key)` short-circuit is the **exact line this story changes.** Today *any* existing row makes the leg non-pending. After this story, an existing row that is **upgrade-eligible** must NOT make the leg non-pending — but **only when the active provider is measured-capable.**

### The change — broaden the predicate (the only production edit)

Replace the bare "does a row exist?" test with "does a *non-upgradeable* row exist?", gated on provider capability:

- The cache projection must now also carry **`Fidelity`** and **`Source`** so eligibility can be evaluated. Change the `existing` projection from `{ FromPoiId, ToPoiId, TravelMode }` to also include `r.Fidelity` and `r.Source`, and build a `Dictionary<(int,int,string), (string Fidelity, string Source)>` keyed by the leg key (instead of, or in addition to, the `have` hash-set). Keep it `AsNoTracking()`.
- Read the active provider's capability **once** per pass (it is constructor-injected, immutable): `var measuredCapable = provider.ProducesMeasuredFidelity;`
- Define upgrade-eligibility precisely (AD-2):
  ```csharp
  // A cached row is eligible for measured upgrade iff it is a low-fidelity,
  // self-produced estimate — never a Manual/Measured (protected) or any other source.
  static bool IsUpgradeEligible(string fidelity, string source) =>
      (fidelity is Fidelity.Estimated or Fidelity.Placeholder)
      && (source is TravelTimeSource.Mock or TravelTimeSource.EstimatedFallback);
  ```
  Confirm the exact constant names while reading the entities: `Fidelity.Estimated`, `Fidelity.Placeholder` (string constants in `Data.Entities`), `TravelTimeSource.Mock`, `TravelTimeSource.EstimatedFallback`. (`TravelTimeSource.Mock` is the source the Mock provider stamps — verify it exists as a constant; the tests reference the literal `"Mock"` and `TravelTimeSource.EstimatedFallback` already.)
- Rewrite the enqueue decision so a leg is pending when **(a)** no row exists, **OR (b)** a row exists, it is upgrade-eligible, **AND** `measuredCapable` is true:
  ```csharp
  var key = (from.PoiId, to.PoiId, mode);
  if (!seen.Add(key))
  {
      continue; // already queued this pass
  }

  if (cached.TryGetValue(key, out var row))
  {
      // A row exists. It is pending only if we can upgrade it to a measured value.
      if (!(measuredCapable && IsUpgradeEligible(row.Fidelity, row.Source)))
      {
          continue; // not cached-and-upgradeable ⇒ leave it alone
      }
  }
  pending.Add(new PendingLeg(from, to, mode));
  ```
  **Watch the `seen.Add` ordering.** Today the code does `have.Contains(key) || !seen.Add(key)` — a single short-circuit. Preserve the "seen this pass" dedupe exactly (a leg must still never be enqueued twice in one pass), and make sure `seen.Add` is still called for every candidate key so the dedupe set is consistent regardless of which branch decides pending-ness. The snippet above adds `seen` first, then evaluates the cache — that ordering keeps dedupe correct.
- **When `measuredCapable` is false**, `(measuredCapable && ...)` is always false, so any existing row makes the leg non-pending — i.e. the behavior is **byte-for-byte the old behavior** (no row ⇒ pending; any row ⇒ skip). This is AC 2's "collapses to existing behavior for Mock."

### What must NOT change

- **`DirectionalPairs` (lines ~183–204)** — the leg set (consecutive pairs + Roundtrip closing leg, finish-distinct handling) is untouched. AC 5. The doc comment on `DirectionalPairs` says *"Mirrors `TripViewModel.BuildLegs`"* — that mirror, plus the MCP `GetTrip` projection, is the AD-2 three-site mirror; verify by reading all three that none needs to change (they don't — this story is purely about eligibility, not leg topology).
- **`UpsertAsync` no-downgrade guard (lines ~213–227)** — `if (existing is not null && existing.Fidelity is Fidelity.Manual or Fidelity.Measured) return;` stays exactly as-is. AC 3. It is the defensive belt: even though the broadened predicate never *selects* a Manual/Measured row (they fail `IsUpgradeEligible`), the guard guarantees no downgrade if anything slips through.
- **The degrade catch in `ProcessOnceAsync` (lines ~83–95)** — already does exactly what AC 4 requires: `catch (OperationCanceledException) when (ct.IsCancellationRequested) throw;` then a general `catch (Exception ex)` that sets `result = EstimatedTravelTime.Compute(...)` and `source = TravelTimeSource.EstimatedFallback` and logs a warning, per-leg, without rethrowing. **`ValhallaRouteUnavailableException` is a plain `Exception`, so it is already caught by this branch** — verify this is true (it does not derive from `OperationCanceledException`) and add a test that proves a Valhalla-style failure degrades. No code change to the catch is expected; if the existing catch already satisfies AC 4, say so explicitly in the Dev Agent Record rather than editing it.
- **Class-level XML doc comment (lines ~11–17)** — currently says *"Computes only if no cache row exists yet for the … key."* **Update this one sentence** to reflect the broadened, capability-gated rule (e.g. "…computes when no cache row exists, or when a measured-capable provider can upgrade an existing low-fidelity Estimated/Placeholder row from Mock/EstimatedFallback"). This is the only doc-comment edit required.

### Verified existing contracts (read before coding)

- **`ITravelTimeProvider.ProducesMeasuredFidelity`** (`Services/Trip/ITravelTimeProvider.cs`) — the `bool` seam member added by Story 2.1. `MockTravelTimeProvider` returns `false`; `ValhallaTravelTimeProvider` returns `true` (Story 2.2). The service already has `provider` constructor-injected (ctor line ~22).
- **`Fidelity`** constants (`Data.Entities`) — `Fidelity.Manual`, `Fidelity.Measured`, `Fidelity.Estimated`, `Fidelity.Placeholder` (string constants).
- **`TravelTimeSource`** constants — `TravelTimeSource.EstimatedFallback` is used today (line ~91); confirm `TravelTimeSource.Mock` exists (the Mock provider's `Source`). The existing tests assert `row.Source.Should().Be(TravelTimeSource.Mock)` and `TravelTimeSource.EstimatedFallback`, so both constants are in use.
- **`RouteSegment`** entity — carries `FromPoiId`, `ToPoiId`, `TravelMode`, `DurationSeconds`, `DistanceMeters`, `GeometryPolyline`, `Fidelity`, `Source`, `ComputedAt`. The projection just needs `Fidelity` + `Source` added.
- **`EstimatedTravelTime.Compute(from, to, mode, options.Value)`** — the smart-haversine edge (now detour-adjusted per Epic 1) reused by the degrade catch; unchanged.
- **`ProcessOnceAsync` / `UpsertAsync` are `internal`** (InternalsVisibleTo) so tests drive them directly without the hosted loop — the existing test class already does this.

## Constraints (NFRs)

- **AD-2 — Capability-gated recompute.** This story *is* AD-2: broaden the pending-leg trigger from "no row" to "no row OR upgrade-eligible", where upgrade-eligible = `Fidelity ∈ {Estimated, Placeholder}` AND `Source ∈ {Mock, EstimatedFallback}`, gated on `ProducesMeasuredFidelity==true` to prevent perpetual Mock rework. Upsert guard unchanged. Three-site leg-projection mirror stays aligned.
- **NFR-10 — Reliability / graceful degradation.** A single leg's provider failure degrades to the smart-haversine estimate and never fails the batch (`[TRIP-DEGRADE-01]`); the cache upsert never downgrades a Manual or Measured row (`[TRIP-MANUAL-01]`); an Estimated/EstimatedFallback row remains eligible for later upgrade once the provider is reachable (this story makes that eligibility real).
- **NFR-11 — Canonical units.** Seconds + meters, conversion only at the provider edge. The degrade path reuses `EstimatedTravelTime.Compute` (already canonical). Unchanged.
- **NFR-12 — Build discipline.** Clean under `TreatWarningsAsErrors` + analyzers; new/changed members keep XML doc style; no group-B violations.
- **NFR-13 — DI seam integrity.** No DI change in this story, but the Trip integration filter must stay green (the service is composed in the integration host).
- **Additive / no regression.** Leg topology, upsert guard, and degrade catch are unchanged in behavior. Only the *pending decision* and one doc-comment sentence change. No DI, no config, no schema change.

## Testing

Extend `LucidCartographer.Tests/Services/TravelTimeComputationBackgroundServiceTests.cs` (flat `namespace LucidCartographer.Tests`). The file already has the scaffolding you need: `BuildService(...)` (accepts a custom `ITravelTimeProvider`), `SeedTwoStopRoundtrip()` / `SeedRoundtripWithModes(...)` / `SeedDriveOpenPath(stops)`, `NoRetryPipelines()`, and the `ThrowingProvider` / `ThrowOnFirstLegProvider` stubs. Mirror the existing test style (FluentAssertions, `ProcessOnceAsync` driven directly).

You will need a **measured-capable** stub provider, since `MockTravelTimeProvider.ProducesMeasuredFidelity == false`. Add a small `sealed` stub (e.g. `MeasuredStubProvider`) implementing `ITravelTimeProvider` with `ProducesMeasuredFidelity => true`, `Source => "ValhallaStub"` (or a measured-looking source), and `GetLegAsync` returning a `TravelLegResult(..., Fidelity.Measured, "<polyline>")`. Use it to prove upgrade-eligible rows get recomputed to Measured.

Cover (AC 6):

- **Upgrade-eligible row IS recomputed when provider is measured-capable** — seed an existing `(1,2,Drive)` row with `Fidelity.Estimated` + `Source = TravelTimeSource.Mock` (or `EstimatedFallback`) for a ground leg, run `ProcessOnceAsync` with the measured-capable stub, assert the row is now `Fidelity.Measured` with the stub's source/geometry (it was re-enqueued and upserted over the low-fidelity estimate).
- **Same upgrade-eligible row is LEFT ALONE when provider is Mock** — identical seed, but run with the default `MockTravelTimeProvider` (`ProducesMeasuredFidelity==false`); assert the row is byte-for-byte unchanged (same duration/fidelity/source), proving no perpetual Mock re-churn (AC 2). This is the regression guard against an infinite recompute loop.
- **`Manual` row is never re-enqueued** — seed a `Fidelity.Manual` / `Source = "Manual"` row for the ground leg, run with the measured-capable stub; assert the Manual row is untouched (it fails `IsUpgradeEligible`, so it is not even queued; the `UpsertAsync` guard is the second line of defense). Assert duration/fidelity/source preserved.
- **`Measured` row is never re-enqueued** — seed a `Fidelity.Measured` / `Source = "OSRM"` (or `"Valhalla"`) row, run with the measured-capable stub; assert it is untouched (a Measured row is not upgrade-eligible — never re-measured). This prevents needless re-routing of already-measured legs.
- **Eligible-but-wrong-source is left alone** — (boundary) seed `Fidelity.Estimated` with a `Source` that is NOT Mock/EstimatedFallback (e.g. `"Valhalla"`); run with the measured-capable stub; assert untouched (the source half of the predicate matters).
- **A failing leg degrades without aborting the batch** — reuse `SeedDriveOpenPath(stops: 3)` + `ThrowOnFirstLegProvider` (or a measured-capable throwing stub) with `NoRetryPipelines()`; assert `ProcessOnceAsync` does not throw, the first leg lands an `EstimatedFallback`/`Estimated` row, and the second leg still computes (`[TRIP-DEGRADE-01]`). The existing `ProcessOnce_LegAfterAThrowingOne_StillComputes` already covers the topology; add/confirm a variant where the throwing provider is **measured-capable** so the degrade is exercised on the new recompute arm.
- **Valhalla-style failure degrades** — a measured-capable stub whose `GetLegAsync` throws `ValhallaRouteUnavailableException` (or a plain `Exception`) degrades the leg to `EstimatedFallback` without aborting (AC 4). Confirms `ValhallaRouteUnavailableException` is caught by the general degrade branch, not the cancellation re-throw.

Keep the existing tests green — especially `ProcessOnce_IsIdempotent_NoDuplicateRowsOnRerun` (a Mock pass must still be idempotent: with `ProducesMeasuredFidelity==false` the second pass must not re-enqueue the Estimated rows it just wrote) and `UpsertAsync_NeverDowngrades_ExistingHigherTrustRow`.

Run the fast suite and the Trip integration filter; both must stay green (NFR-13).

## Build/Test commands

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast tests: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- This service's tests only: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~TravelTimeComputationBackgroundService"`
- Trip integration: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`

## Dev Notes

- **The whole story is one predicate broaden plus a richer cache projection.** Read `LoadPendingLegsAsync` end-to-end first; the single line that changes is `if (have.Contains(key) || !seen.Add(key)) continue;`. Everything else (collection load, stop projection, `DirectionalPairs`, ground-mode filter, `UpsertAsync`, degrade catch) is unchanged.
- **The capability gate is what stops the infinite loop.** Without `&& measuredCapable`, a Mock deployment would re-enqueue its own Estimated rows every pass forever (Mock writes Estimated → next pass sees an upgrade-eligible Estimated/Mock row → re-enqueues → writes Estimated again …). Gating the broadened arm on `ProducesMeasuredFidelity==true` is the AD-2 guard against this — the "left alone when Mock" test is the load-bearing regression for it.
- **Why `Placeholder` is in the eligible set:** an Air/AnyAir leg is never enqueued (the `IsGroundMode` filter excludes it), so a `Placeholder` row only reaches eligibility for a *ground* leg that was previously degraded oddly; including `Placeholder` in the predicate matches AD-2's literal definition and is harmless because ground legs are the only ones queued. Keep the predicate faithful to AD-2 even though ground+Placeholder is rare.
- **The upsert guard already protects Manual/Measured** — the broadened predicate also never selects them (they fail `IsUpgradeEligible`). Belt and suspenders; do not remove either.
- **`ValhallaRouteUnavailableException` needs no special catch** — it is a plain `Exception` (Story 2.2), so the existing general `catch (Exception)` in `ProcessOnceAsync` already degrades it. Verify by reading the exception's base type; add a test, not a catch clause.
- **No DI / no config in this story.** If you reach for `TripServicesExtensions`/`AddTripServices` or `appsettings.json`, stop — that is Story 2.4. This story ends at the broadened predicate + tests, all under the current DI (the integration host composes Mock, so the integration filter exercises the `measuredCapable==false` collapse path).
- **AD-2 mirror check.** After the change, re-read `TravelTimeComputationBackgroundService.DirectionalPairs`, `TripViewModel.BuildLegs`, and the MCP `GetTrip` projection to confirm the leg set is identical across all three (it must be — this story does not touch topology). Note the confirmation in the Dev Agent Record.

### References

- [Source: _bmad-output/planning-artifacts/epics.md#Story 2.3] — acceptance criteria
- [Source: _bmad-output/planning-artifacts/epics.md#Additional Requirements] — AD-2 (capability-gated recompute trigger, upgrade-eligibility definition, mirror check), AD-3 (ProducesMeasuredFidelity origin)
- [Source: _bmad-output/planning-artifacts/architecture.md] — AD-2 pending-leg predicate broadening + three-site projection mirror
- [Source: LucidCartographer/Services/Trip/TravelTimeComputationBackgroundService.cs] — `LoadPendingLegsAsync` (`have.Contains(key)` predicate to broaden), `ProcessOnceAsync` (degrade catch, unchanged), `UpsertAsync` (no-downgrade guard, unchanged), `DirectionalPairs` (leg shape, unchanged), class XML doc (one sentence to update)
- [Source: LucidCartographer/Services/Trip/ITravelTimeProvider.cs] — `ProducesMeasuredFidelity` seam bool (the capability gate)
- [Source: LucidCartographer.Tests/Services/TravelTimeComputationBackgroundServiceTests.cs] — existing test scaffolding (`BuildService`, seed helpers, `ThrowingProvider`/`ThrowOnFirstLegProvider`, `NoRetryPipelines`, `UpsertAsync_NeverDowngrades_ExistingHigherTrustRow`)
- [Source: _bmad-output/implementation-artifacts/stories/story-2-1-provider-capability-seam-valhalla-source-and-attribution-scaffolding.md] — Story 2.1 added the `ProducesMeasuredFidelity` member this story gates on
- [Source: _bmad-output/implementation-artifacts/stories/story-2-2-valhallatraveltimeprovider-measured-all-ground-modes.md] — Story 2.2 added `ValhallaTravelTimeProvider` (ProducesMeasuredFidelity=true) and `ValhallaRouteUnavailableException` (plain Exception → caught by the degrade branch)

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (dev-story workflow)

### Debug Log References

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug` → 0 Warning(s), 0 Error(s) (clean under TreatWarningsAsErrors).
- Service tests: `--filter "FullyQualifiedName~TravelTimeComputationBackgroundService"` → 25 passed, 0 failed.
- Fast suite: `--filter "FullyQualifiedName!~Integration"` → 1021 passed, 0 failed, 0 skipped.
- Trip integration: `--filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"` → 20 passed, 0 failed, 0 skipped.

### Completion Notes List

- **AC1 — pending-leg predicate broadened.** `LoadPendingLegsAsync` now projects each cache row's `Fidelity` + `Source` into a `Dictionary<(int,int,string),(string Fidelity,string Source)>` (`cached`, `AsNoTracking()`), replacing the bare `have` hash-set. The enqueue decision is: pending iff no row exists, OR a row exists that is upgrade-eligible AND the provider is measured-capable. A non-upgradeable existing row leaves the leg alone.
- **AC2 — capability gate.** `measuredCapable = provider.ProducesMeasuredFidelity` is read once per pass. The broadened arm is `measuredCapable && IsUpgradeEligible(...)`; when `measuredCapable` is false the term is always false, so any existing row makes the leg non-pending — byte-for-byte the legacy "no row ⇒ pending; any row ⇒ skip" behavior (verified by `ProcessOnce_UpgradeEligibleRow_IsLeftAlone_WhenProviderMock` and the still-green `ProcessOnce_IsIdempotent_NoDuplicateRowsOnRerun`).
- **AC3 — upsert guard unchanged.** `UpsertAsync`'s `if (existing is not null && existing.Fidelity is Fidelity.Manual or Fidelity.Measured) return;` is untouched. The broadened predicate also never selects Manual/Measured (they fail `IsUpgradeEligible`), so the guard is the defensive belt behind the read. `UpsertAsync_NeverDowngrades_ExistingHigherTrustRow` stays green.
- **AC4 — degrade path verified, no production change.** The existing `ProcessOnceAsync` catch (`catch (OperationCanceledException) when (ct.IsCancellationRequested) throw;` then general `catch (Exception)` → `EstimatedTravelTime.Compute` + `Source = EstimatedFallback`, per-leg, no rethrow) already satisfies AC4. Confirmed `ValhallaRouteUnavailableException : Exception` (plain Exception, not OperationCanceledException) so it is caught by the general degrade branch. Added tests proving a measured-capable throwing provider degrades the failing leg without aborting the batch, and that a `ValhallaRouteUnavailableException` degrades to `EstimatedFallback`. No edit to the catch.
- **AC5 — leg shape unchanged / three-site mirror aligned.** `DirectionalPairs` is untouched (consecutive k→k+1 + closing leg back to first on Roundtrip, distinct-Finish handling). Re-read `TripViewModel.BuildLegs` (Components/Shared/Trip/TripViewModel.cs ~919) — identical topology (N-1 consecutive + closing leg when no distinct Finish). The MCP `GetTrip` projection is likewise topology-only and unaffected by an eligibility-only change. This story touches only the pending/eligibility decision, not which legs exist.
- **AC6 — unit tests added** (see File List): upgrade-eligible recomputed when measured-capable (Mock + EstimatedFallback sources); same row left alone under Mock; Manual + Measured never re-enqueued under a measured-capable provider; eligible-fidelity-but-wrong-source boundary left alone; failing leg degrades without aborting batch (measured-capable throwing provider); Valhalla-style failure degrades to EstimatedFallback.
- **AC7 — clean build + green suites.** 0/0 under TreatWarningsAsErrors (no MA0002/MA0015/MA0046/MA0047/MA0074/VSTHRD200); fast suite and Trip integration both green.
- **Class XML doc** updated to describe the broadened, capability-gated rule (the only doc-comment edit). Added the `IsUpgradeEligible(fidelity, source)` private static helper next to `IsGroundMode`.
- **No DI / no config / no schema change** (those are Story 2.4) — production change is single-file and surgical.

### File List

- `LucidCartographer/Services/Trip/TravelTimeComputationBackgroundService.cs` (modified) — broadened pending-leg predicate (richer cache projection + capability gate + `IsUpgradeEligible` helper); class XML doc updated. Degrade catch, `UpsertAsync` guard, and `DirectionalPairs` unchanged.
- `LucidCartographer.Tests/Services/TravelTimeComputationBackgroundServiceTests.cs` (modified) — added `MeasuredStubProvider`, `MeasuredThrowingProvider`, `DegradeOnFirstLegProvider` stubs and `SeedExistingLegAsync` helper; added AC-6 tests for recompute/left-alone/protected/boundary/degrade.

### Change Log

| Date       | Change |
|------------|--------|
| 2026-06-24 | Story drafted (create-story): capability-gated recompute trigger + degrade. Status → ready-for-dev. |
| 2026-06-24 | dev-story: broadened LoadPendingLegsAsync predicate to "no row OR upgrade-eligible" gated on ProducesMeasuredFidelity (AD-2); added IsUpgradeEligible helper + richer cache projection; class XML doc updated. Degrade catch / UpsertAsync guard / DirectionalPairs unchanged (AC3/AC4/AC5). Added 8 unit tests (AC6). Build clean (0/0); fast 1021/1021, Trip integration 20/20. Status → review. |
| 2026-06-24 | Senior Developer Review (AI): APPROVED. All 7 ACs verified implemented; 0 Critical / 0 High / 0 Medium / 2 Low (stale doc counts, cosmetic ToDictionary). Build 0/0; service tests 28/28; fast suite 1024/1024; Trip integration 20/20. Status → done. |

## Senior Developer Review (AI)

**Reviewer:** satec\yurik (autonomous story-automator review)
**Date:** 2026-06-24
**Outcome:** ✅ **APPROVED** — Status → done

### Scope reviewed (Story 2.3 changes only)

- `LucidCartographer/Services/Trip/TravelTimeComputationBackgroundService.cs` — broadened pending-leg predicate, richer cache projection (`Fidelity`+`Source`), `measuredCapable` gate, `IsUpgradeEligible` helper, one-sentence class XML-doc update.
- `LucidCartographer.Tests/Services/TravelTimeComputationBackgroundServiceTests.cs` — new recompute/left-alone/protected/boundary/degrade tests + measured-capable stubs.

Intermingled uncommitted changes from Epic 1 (detour factors) and Stories 2.1/2.2 (provider seam, Valhalla provider, UiStrings, leafletInterop.js, etc.) were explicitly **excluded** — they belong to their own already-reviewed stories.

### Acceptance Criteria verdict

| AC | Verdict | Evidence |
|----|---------|----------|
| AC1 — predicate broadened to "no row OR upgrade-eligible" (Fidelity ∈ {Estimated, Placeholder} AND Source ∈ {Mock, EstimatedFallback}) | ✅ IMPLEMENTED | `LoadPendingLegsAsync` lines 130-188; `IsUpgradeEligible` lines 199-201 faithful to AD-2. |
| AC2 — broadened arm gated on `ProducesMeasuredFidelity==true`; collapses to legacy for Mock | ✅ IMPLEMENTED | `measuredCapable` read once (line 143); `!(measuredCapable && IsUpgradeEligible(...))` at 179-185. Proven by `ProcessOnce_UpgradeEligibleRow_IsLeftAlone_WhenProviderMock` + `ProcessOnce_IsIdempotent_NoDuplicateRowsOnRerun`. |
| AC3 — upsert no-downgrade guard unchanged ([TRIP-MANUAL-01]) | ✅ IMPLEMENTED | `UpsertAsync` guard lines 252-255 byte-identical; `UpsertAsync_NeverDowngrades_ExistingHigherTrustRow` green. |
| AC4 — per-leg degrade to EstimatedFallback, Valhalla failure caught, cancellation re-throws | ✅ IMPLEMENTED | Catch block 85-97 unchanged; `ValhallaRouteUnavailableException : Exception` verified (plain Exception ⇒ caught by general branch); `ProcessOnce_ValhallaRouteUnavailable_DegradesToEstimatedFallback` + `ProcessOnce_MeasuredCapableProviderThrows_DegradesLeg_WithoutAbortingBatch`. |
| AC5 — leg shape / three-site mirror unchanged | ✅ IMPLEMENTED | `DirectionalPairs` untouched; `TripViewModel.cs` and MCP `GetTrip` projection NOT in the diff (only sibling test files churn). Topology-only invariant preserved. |
| AC6 — unit tests cover recompute/left-alone/protected/degrade | ✅ EXCEEDED | 28 service tests incl. extra Placeholder-upgrade + mixed-batch coverage beyond the documented set. |
| AC7 — clean build + green suites | ✅ VERIFIED | Build 0 Warning / 0 Error (TreatWarningsAsErrors); fast 1024/1024; Trip integration 20/20. |

### Findings

**Critical:** 0 &nbsp;|&nbsp; **High:** 0 &nbsp;|&nbsp; **Medium:** 0 &nbsp;|&nbsp; **Low:** 2

- 🟢 **LOW (doc accuracy):** Completion Notes / Debug Log report 25 service tests and a 1021-fast-suite count; the working tree actually has 28 service tests and a 1024 fast suite (the dev added 3 extra coverage tests — Placeholder upgrade + mixed-batch ×2 — that strengthen coverage but were under-reported). No functional impact.
- 🟢 **LOW (style):** The manual `foreach` building `cached` (lines 134-138) could be a `ToDictionary`, but the explicit loop is correct and gives deliberate last-write-wins semantics on duplicate keys (avoids `ToDictionary`'s throw). Not worth changing.

No auto-fixes were applied — there were no Critical/High/Medium issues to fix.

### Git vs Story File List

Both review-surface files (`TravelTimeComputationBackgroundService.cs`, `TravelTimeComputationBackgroundServiceTests.cs`) appear as modified in `git status` and are documented in the story File List. No discrepancies on the Story-2.3 surface.

_Reviewer: satec\yurik on 2026-06-24_
