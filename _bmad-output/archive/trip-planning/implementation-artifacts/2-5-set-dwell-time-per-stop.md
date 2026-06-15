---
baseline_commit: c3e93a022d4948cccc8f54168cb6534077fd7a5a
---

# Story 2.5: Set Dwell Time per stop

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a trip planner,
I want to set how long I'll linger at each stop,
so that my timeline reflects time spent, not just time travelling.

## Acceptance Criteria

_(Source: epics.md → Epic 2 → Story 2.5; FR-12, UX-DR3, AR-11)_

1. **Set dwell, persisted per membership.** With Trip View on, a user can set a Dwell Time on a stop. The value is stored in **minutes** on the Collection–POI membership (`PoiCollectionItem.DwellMinutes`), so the same POI can carry a different dwell across trips. The input replaces the inert dwell placeholder on the stop row, on **both** surfaces, is `UiStrings`-labelled and `aria-label`led, and prefills the persisted value. Clearing it sets `DwellMinutes` back to null.

2. **No dwell ⇒ zero contribution.** A stop with no Dwell Time set stores `null` and contributes zero (the timeline that consumes dwell is Story 2.6 — here, "contributes zero" means an unset stop persists `null`, not `0`, and the value is simply stored/displayed).

3. **Overnight is just a large dwell.** A large value (e.g. 600 minutes) is accepted and stored verbatim — there is no special "day"/overnight handling. The input is bounded to a sane maximum so the minutes→(future seconds) math can never overflow.

4. **Available on any stop.** Dwell can be set on any stop in the list, including an **Unplaceable** stop (it has no travel time but can still carry dwell — FR-13/2.6 uses this). The value persists on its membership identically.

5. **Scope & convention gates.** No timeline computation/aggregation here (Story 2.6 walks dwell + travel into arrivals); no travel-time changes. No new migration (`DwellMinutes` already exists, migration `20260611213107_AddTripPlanning`). Build warnings-as-errors, no group-B analyzer violations, no `ConfigureAwait(false)`; all UI text via `UiStrings`; new decisions tagged `TRIP-DWELL-01`; both surfaces updated; canonical units (minutes at the UI edge). The dwell input must NOT regress stop-row selection (2.2 lesson — keep it compact, `stopPropagation`, no layout-shifting siblings).

## Tasks / Subtasks

- [x] **Task 1 — Persist dwell in the ViewModel (AC: 1, 2, 3, 4)**
  - [x] Add `public async Task SetDwellMinutesAsync(int poiId, int? minutes)` to `TripViewModel`, mirroring the 2.2 manual-entry write path (the VM owns `factory` + `SqliteWriteLock`): load the `PoiCollectionItem` for `(ActiveCollectionId, poiId)`, set `DwellMinutes = minutes` (null clears), `SaveChangesAsync` under the write lock, `await RefreshProjectionsAsync(collectionId)`, `Notify()`. Guard: active collection + Trip View on; reject `minutes < 0` or `minutes > MaxDwellMinutes`. Tag `// TRIP-DWELL-01`.
  - [x] Add `internal const int MaxDwellMinutes` (a generous bound, e.g. 60×24×60 = 60 days, matching the manual-leg cap precedent) so a future minutes→seconds conversion (2.6) can't overflow.
  - [x] Do NOT signal the travel-time trigger (dwell does not invalidate route segments — it is independent of travel times).

- [x] **Task 2 — Carry dwell onto the projection (AC: 1, 4)**
  - [x] Add `int? DwellMinutes` to `TripStopRow` (`Components/Shared/Trip/TripProjections.cs`). Populate it in `ReadStopsAndRowsAsync` (`TripViewModel.cs:729-772`) for both placeable and unplaceable rows from the membership's `DwellMinutes`. (The row read already projects membership fields — add `DwellMinutes` to the `Select`.)
  - [x] Keep canonical units (store/carry minutes); presentation only.

- [x] **Task 3 — Dwell input on the stop row, both surfaces (AC: 1, 3, 4, 5)**
  - [x] Replace the inert dwell placeholder with a numeric minutes input: desktop `TripStopList.razor:176` and mobile `MobileTripPanel.razor:187`. Wire `@onchange` → `Vm.SetDwellMinutesAsync(row.PoiId, parsed)`; `value` prefills `row.DwellMinutes`; `@onclick:stopPropagation` + `@onkeydown:stopPropagation` (so editing dwell never selects/reorders the row). `min="0"`, `max="@TripViewModel.MaxDwellMinutes"`, `inputmode="numeric"`. Render on placeable AND unplaceable rows.
  - [x] Keep the input compact and DO NOT add a layout-shifting sibling (e.g. a unit-suffix span) over the row center — that broke Playwright row-selection in 2.2. Parse the change value with `CultureInfo.CurrentCulture`; empty/blank ⇒ null (clear).
  - [x] `UiStrings`: a dwell input `aria-label` ({0} = stop name) and placeholder. No hardcoded text. (Reuse/repoint the existing `TripDwellAria`; the old `TripDwellPlaceholder` "—" is superseded by the input's own placeholder.)

- [x] **Task 4 — Tests (AC: all)**
  - [x] **Unit/VM:** `SetDwellMinutesAsync` persists `DwellMinutes` on the correct `PoiCollectionItem` (and only that one); null clears; a value round-trips into `TripStopRow.DwellMinutes` after refresh; same POI in two collections carries independent dwell; out-of-range (negative / > max) is rejected (no write). Setting dwell does NOT signal the travel-time trigger / change any `RouteSegment` row.
  - [x] **Unit/VM:** dwell can be set on an **unplaceable** stop's membership and round-trips.
  - [x] **Component (bUnit), both surfaces:** the dwell input renders with the persisted value, `UiStrings`-labelled; entering a value invokes the VM; the input is present on placeable and unplaceable rows; editing dwell does not select the row (stopPropagation).
  - [x] **Integration (Trip):** confirm row selection still works with the dwell input present (the existing selection test must stay green — the dwell input has `stopPropagation`, like the 2.2 manual input). Full unit/component suite green; Trip integration green; no new analyzer warnings.

## Dev Notes

### Scope guardrails
- **In scope:** persist per-stop dwell (minutes) on `PoiCollectionItem.DwellMinutes`, a dwell input on every stop row (both surfaces) that prefills/clears, projection carry, tests.
- **OUT of scope:** the itinerary timeline that consumes dwell (arrivals, departures, totals) — that is Story 2.6. Do NOT compute arrivals or sum dwell here. No travel-time/cache changes. No migration.

### Built on prior stories (reuse, don't reinvent)
- `PoiCollectionItem.DwellMinutes` (`Data/Entities/PoiCollectionItem.cs:18`, `int?`) already exists — no migration. It lives on the membership, so the same POI carries different dwell per collection (AC1).
- `TripViewModel.SetManualLegTimeAsync` (Story 2.2, `:1111`) is the exact write-under-lock + refresh + `Notify` pattern to mirror (minus the trigger signal — dwell doesn't touch travel times). `MaxManualLegMinutes` is the precedent for `MaxDwellMinutes`.
- The dwell placeholder is rendered at `TripStopList.razor:176` (desktop `<span>`) and `MobileTripPanel.razor:187` (mobile `<div class="trailing">`) with `aria-label="@UiStrings.TripDwellAria"` and text `@UiStrings.TripDwellPlaceholder`. Replace those with the input.
- `TripStopRow` (`TripProjections.cs`) currently `(int? DisplayOrder, int PoiId, string Name, bool IsPlaceable)` — add `int? DwellMinutes`. `ReadStopsAndRowsAsync` (`TripViewModel.cs:729-772`) builds rows for unplaceable then placeable — add `DwellMinutes` to both `Select`s (the membership is already in scope there).
- The 2.2 manual-time input is the template for a row-embedded numeric input that doesn't break selection: `@onclick:stopPropagation` + `@onkeydown:stopPropagation`, compact width, NO unit-suffix span (the suffix span is what broke selection in 2.2 — do not repeat it).

### Architecture & conventions (project-context.md)
- Layering Component→ViewModel→Service→Data; the input binds to a VM method; the VM writes the membership under the write lock.
- Build discipline: warnings-as-errors, no group-B analyzer violations, no `ConfigureAwait(false)`, `CultureInfo.CurrentCulture` on parse. Tag `TRIP-DWELL-01`.
- i18n: dwell aria/placeholder via `UiStrings`. a11y: `aria-label` on the input; both desktop and `Mobile*`. Units: minutes at the UI edge (no seconds conversion in this story — 2.6 converts when it sums).
- The VM ctor is unchanged (no new dependency) — `SetDwellMinutesAsync` uses the existing `factory`/`writeLock`. So NO test-builder/DI churn this time.

### Testing standards
- Unit/VM (persist on the right membership, null clear, per-collection independence, range guard, no trigger signal, unplaceable dwell), Component (bUnit input both surfaces via `MobileTestBase`), and keep the Trip integration selection test green. `InternalsVisibleTo` set; EF InMemory / temp-SQLite.

### Previous-story intelligence
- 2.2: a row-embedded input is fine for selection ONLY with `stopPropagation` and no layout-shifting sibling — the manual-time input proves it works; the suffix-span experiment proves what breaks it. Keep the dwell input equally lean.
- 2.1–2.4 lesson: assert the persisted value + the round-trip into the projection, not just that the method was called.
- Reuse the manual-entry parse/guard idioms; no new helpers needed.

### References
- [Source: _bmad-output/planning-artifacts/epics.md#Story 2.5] — FR-12, UX-DR3
- [Source: _bmad-output/planning-artifacts/epics.md#Additional Requirements] — AR-11 (dwell in minutes; convert at UI edge)
- [Source: _bmad-output/project-context.md]
- [Source: LucidCartographer/Data/Entities/PoiCollectionItem.cs:18], [Components/Shared/Trip/TripProjections.cs] (`TripStopRow`), [Components/Shared/Trip/TripViewModel.cs:729-772 (ReadStopsAndRowsAsync), :1111 (SetManualLegTimeAsync pattern)]
- [Source: LucidCartographer/Components/Shared/Trip/TripStopList.razor:176], [MobileTripPanel.razor:187], [Services/UiStrings.cs (TripDwellAria/TripDwellPlaceholder)]

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (BMAD dev-story; delegated dev subagent + orchestrator verification + fresh-context review)

### Debug Log References

- Build: 0 warnings / 0 errors.
- Unit/component (`!~Integration`): **661 passed** (incl. the added uncomputed-leg invariant test).
- Trip integration (`Integration&Trip`): **19 passed** (selection test green with the dwell input present).

### Completion Notes List

- ✅ AC1 — `SetDwellMinutesAsync` persists `DwellMinutes` on the `(ActiveCollectionId, poiId)` membership under the write lock, refresh + Notify; the input replaces the inert placeholder on both surfaces, prefills + clears (null). Tests `SetDwellMinutes_PersistsOnTheCorrectMembershipOnly_AndRoundTrips`, `..._Null_ClearsTheValue`, bUnit prefill/label.
- ✅ AC2 — unset persists `null` (not 0); `..._Null_ClearsTheValue`.
- ✅ AC3 — large value stored verbatim; `MaxDwellMinutes` (60 days) bound; `..._LargeValue_IsStoredVerbatim`, `..._AtMax_IsAccepted`, `..._OutOfRange_IsRejected_NoWrite`.
- ✅ AC4 — dwell on any stop incl. unplaceable; `..._OnUnplaceableStop_RoundTrips`, bUnit `Dwell_Input_Present_OnUnplaceableRow` (both surfaces).
- ✅ AC5 — no timeline aggregation; no migration; selection preserved (`stopPropagation`, compact `w-10` input); `SamePoiInTwoCollections_IsIndependent`; `Dwell_Input_Editing_DoesNotSelectTheRow`.
- Review (fresh context): **0 CRITICAL / 0 HIGH / 1 MEDIUM / 2 LOW**. Selection-regression and UiStrings-repointing both explicitly PASS; wrong-row write verified absent.
  - [x] [MEDIUM, addressed] The "no signal" test only held under fully-seeded computed legs; a dwell edit calls the shared `RefreshProjectionsAsync` which *can* wake the compute loop when a leg is still computing. This is the pre-existing `IsAnyLegComputing` re-kick (a harmless no-op re-check), **not** a dwell-driven recompute — the dwell path itself touches no `RouteSegment` and makes no provider call. **Resolved:** clarified the `SetDwellMinutesAsync` doc to state the real invariant, and added `SetDwellMinutes_WithUncomputedLegs_TouchesNoRouteSegments` proving zero `RouteSegment` rows are created/invalidated even with uncomputed legs (while the value still round-trips).
  - [ ] [LOW, accepted] One-way `value=` + `@onchange` (not `@bind`): a rejected entry stays visible until the next render — matches the existing 2.2 manual-input precedent; consistent, not a new regression.
  - [ ] [LOW, n/a] Story checkboxes/File List were blank at review time — populated here (orchestrator-owned).

**Deviations / decisions:**
- Desktop dwell input narrowed to `w-10` (vs the manual input's `w-12`): `w-12` overflowed the crowded `w-64` panel row and collapsed the `flex-1 truncate` name span — the 2.2-style layout-shift that breaks `TripView_ShowsStopListPanel_BesideMap…`. `w-10` keeps the name truncation + row selection green. Mobile reuses the existing `.trailing` width.
- Unplaceable rows omit `stopPropagation` on the dwell input by design — those rows carry no click/keydown selection handler, so there is nothing to stop.
- `TripDwellAria`→"Dwell time in minutes at {0}", `TripDwellPlaceholder`→"min" (both consumers updated; no stale zero-arg call).
- Boundary: Story 2.6 consumes `TripStopRow.DwellMinutes`/`PoiCollectionItem.DwellMinutes`, converting minutes→seconds when it aggregates with travel times; `MaxDwellMinutes` bounds that future conversion.

### File List

**New (1):**
- `LucidCartographer.Tests/ViewModels/TripViewModelDwellTests.cs`

**Modified (6):**
- `LucidCartographer/Components/Shared/Trip/TripProjections.cs` (`TripStopRow.DwellMinutes`)
- `LucidCartographer/Components/Shared/Trip/TripViewModel.cs` (`MaxDwellMinutes`, `SetDwellMinutesAsync`, `PersistDwellMinutesAsync`, dwell in `ReadStopsAndRowsAsync`)
- `LucidCartographer/Components/Shared/Trip/TripStopList.razor` (desktop dwell input)
- `LucidCartographer/Components/Shared/Trip/MobileTripPanel.razor` (mobile dwell input)
- `LucidCartographer/Services/UiStrings.cs` (`TripDwellAria`/`TripDwellPlaceholder` repointed)
- `LucidCartographer.Tests/Components/Trip/TripStopListTests.cs` (dwell bUnit tests + stale placeholder assertions)

## Change Log

| Date | Change |
|------|--------|
| 2026-06-14 | Story 2.5 implemented: per-stop dwell input (both surfaces) persisted to `PoiCollectionItem.DwellMinutes` (minutes), on placeable + unplaceable rows, with `MaxDwellMinutes` bound; projection carry; no timeline/travel-time coupling; no migration. Build clean; 660 unit/component + 19 Trip integration green. Status → review. |
| 2026-06-14 | Adversarial review (fresh context): 0 CRITICAL/0 HIGH/1 MEDIUM/2 LOW. Clarified the dwell/recompute invariant in doc + a new uncomputed-leg test; 2 LOW accepted. 661 unit/component green. Status → done. |

## Senior Developer Review (AI)

**Outcome:** Approve (done) — 0 CRITICAL / 0 HIGH; 1 MEDIUM addressed, 2 LOW accepted.
**Reviewer:** Fresh-context adversarial reviewer (separate agent from the implementer).
**Date:** 2026-06-14

**Findings & resolution:**
- [x] [MEDIUM] "no signal" test held only under seeded-computed legs. **Resolved** — invariant clarified in `SetDwellMinutesAsync` doc + `SetDwellMinutes_WithUncomputedLegs_TouchesNoRouteSegments` proves no `RouteSegment` is touched/recomputed regardless of compute state.
- [ ] [LOW, accepted] One-way `value=`/`@onchange` (matches 2.2 manual-input precedent).
- [ ] [LOW] Story doc tracking populated here.

**Explicit verdicts:** selection-regression PASS (placeable rows carry `stopPropagation`; unplaceable rows have no selection handler; `w-10` keeps name truncation); UiStrings repointing PASS (only the two razor consumers + tests, `{0}` satisfied). Wrong-row write absent (keys on collection id + poi id). All 5 ACs hold. No scope leakage into 2.6.
