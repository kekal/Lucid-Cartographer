---
title: 'Trip View toggle gates on collection membership (≥1), not viewport count'
type: 'bugfix'
created: '2026-06-16'
status: 'done'
context: ['{project-root}/_bmad-output/project-context.md']
baseline_commit: '314d14a6f0665c32020b8644c1b4f454cf625999'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The Trip View toggle button is offered only when ≥2 placeable POIs fall inside the *current map viewport*. It should be offered when exactly one collection is selected and that collection has ≥1 placeable POI — independent of map pan/zoom. The Trip stop list already shows the full collection regardless of map state, so gating the button on the viewport count is inconsistent: panning away wrongly hides the toggle.

**Approach:** Feed the Trip gate the single visible collection's *full placeable membership* count (viewport-independent) instead of the viewport-filtered count, and lower the gate threshold from ≥2 to ≥1. The same threshold drives both the button availability and the [TRIP-GATE-01] auto-disable, so both move in lockstep.

## Boundaries & Constraints

**Always:**
- The button is available iff exactly one collection is visible (no active search) AND that collection has ≥1 placeable (lat+lon present) POI, regardless of viewport.
- Keep the button-availability gate and the [TRIP-GATE-01] auto-disable gate on the *same* threshold and the *same* count — they are two faces of one UX-DR1 gate.
- Keep the desktop and mobile toggles behaviourally identical (both read `Vm.IsToggleAvailable`).

**Ask First:**
- (none)

**Never:**
- Do NOT change the Trip stop list content, the POI list mode (`PoiTable`), viewport filtering (`FilteredPois`/`ApplyViewportFilter`), Stop ordering, legs, or any other Trip logic. This is the visibility gate only.
- Do NOT remove or repurpose the existing viewport `MapPageViewModel.PlaceablePoiCount` — add a new property alongside it.
- Do NOT change the `TripViewModel` public method signatures (`LoadAsync`, `UpdatePlaceableCount`, `RefreshAfterMembershipChangeAsync` still take a count) — only the *value* the host feeds them changes.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Single collection, 1 placeable POI | exactly one collection visible, no search | Toggle visible (was hidden) | N/A |
| Single collection, panned so 0 POIs in view | collection has ≥1 placeable, viewport empty | Toggle still visible | N/A |
| Single collection, 0 placeable POIs | one collection visible, all members lack coords | Toggle absent (never an error) | N/A |
| Multiple collections visible | ≥2 collections visible | Toggle absent (single-collection rule) | N/A |
| Active search | search query set | Toggle absent (`SingleVisibleCollectionId` null) | N/A |
| Enabled trip drops to 0 placeable | last placeable member removed while ON | Auto-disable Trip View, persist off, announce | persist failure logged, overlays still cleared |

</frozen-after-approval>

## Code Map

- `LucidCartographer/Components/Shared/Trip/TripViewModel.cs` -- `IsToggleAvailable` (L85) and `AutoDisableBelowGateAsync` (L625): the `>= 2` gate → `>= 1`. `PlaceableCount` now carries the collection-membership count fed by the host.
- `LucidCartographer/Components/Pages/MapPageViewModel.cs` -- add `CollectionPlaceablePoiCount` (placeable count over `VisiblePois` when `SingleVisibleCollectionId` is set, else 0). `VisiblePois` is the single visible collection's full membership; `FilteredPois` is its viewport subset.
- `LucidCartographer/Components/Pages/MapPage.razor` -- `SyncTripAsync` (L586-597) and `OnMembershipChangedAsync` (L606): feed `Vm.CollectionPlaceablePoiCount` to `LoadAsync`/`UpdatePlaceableCount`/`RefreshAfterMembershipChangeAsync` instead of `Vm.PlaceablePoiCount`.
- `LucidCartographer/Components/Shared/Trip/TripToggle.razor` & `MobileTripToggle.razor` -- header comments say "below 2 placeable POIs"; update to the ≥1 / collection-membership wording.
- `LucidCartographer.Tests/ViewModels/TripViewModelTests.cs` -- gate tests assume ≥2 (L153-164 availability flip; L171-195 auto-disable; comment header L166-169).
- `LucidCartographer.Tests/Components/Trip/TripToggleTests.cs` -- `IsHidden_WhenFewerThanTwoPlaceable` (L54-63) assumes 1 → hidden.

## Tasks & Acceptance

**Execution:**
- [x] `LucidCartographer/Components/Pages/MapPageViewModel.cs` -- add `CollectionPlaceablePoiCount` returning `SingleVisibleCollectionId is null ? 0 : VisiblePois.Count(p => p is { Latitude: not null, Longitude: not null })`, with a doc comment. -- viewport-independent source for the gate.
- [x] `LucidCartographer/Components/Pages/MapPage.razor` -- switch both Trip count feeds (`SyncTripAsync`, `OnMembershipChangedAsync`) from `Vm.PlaceablePoiCount` to `Vm.CollectionPlaceablePoiCount`. -- gate now tracks the collection, not the viewport.
- [x] `LucidCartographer/Components/Shared/Trip/TripViewModel.cs` -- change both `>= 2` gate checks (L85, L625) to `>= 1`; update XML docs/comments that say "≥2" / "at least two placeable". -- enable single-POI trips; keep button + auto-disable in lockstep.
- [x] `LucidCartographer/Components/Shared/Trip/TripToggle.razor` + `MobileTripToggle.razor` -- update the "below 2 placeable POIs" header comments to the ≥1 collection-membership rule. -- keep comments truthful.
- [x] `LucidCartographer.Tests/ViewModels/TripViewModelTests.cs` -- retarget gate tests to the ≥1 threshold (availability flips at 1→0; auto-disable fires on drop to 0, stays enabled at 1); add a case that 1 placeable ⇒ available; fix the comment header. -- lock the new gate.
- [x] `LucidCartographer.Tests/Components/Trip/TripToggleTests.cs` -- 1 placeable ⇒ toggle visible; add a 0-placeable ⇒ hidden case. -- component-level gate coverage.

**Acceptance Criteria:**
- Given one collection visible with exactly 1 placeable POI, when the map page renders, then the Trip View toggle is present (desktop and mobile).
- Given Trip View is on for a single-POI collection, when the user pans so no POI is in view, then the toggle stays present and Trip View stays on.
- Given one collection visible whose members all lack coordinates, when the page renders, then the toggle is absent (no error affordance).
- Given Trip View is on, when the last placeable member is removed (membership change to 0), then Trip View auto-disables, persists off, and announces.

## Verification

**Commands:**
- `dotnet build LucidCartographer/LucidCartographer.csproj` -- expected: 0 warnings/errors (warnings are build breaks).
- `dotnet test --filter "FullyQualifiedName~TripViewModelTests|FullyQualifiedName~TripToggleTests"` -- expected: all green.
- `dotnet test --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"` -- expected: green (mandatory after any Trip VM/DI change per project-context).
- `dotnet test --filter "FullyQualifiedName~MobileTripViewTests"` -- expected: green (responsive path).

## Suggested Review Order

**The new gate source (design intent)**

- Entry point — viewport-INDEPENDENT placeable count over the lone collection's full membership; returns 0 unless exactly one collection is visible.
  [`MapPageViewModel.cs:85`](../../LucidCartographer/Components/Pages/MapPageViewModel.cs#L85)

**The gate itself**

- Single shared threshold so "offer" and "auto-disable" edges can never drift apart.
  [`TripViewModel.cs:80`](../../LucidCartographer/Components/Shared/Trip/TripViewModel.cs#L80)

- Button availability — now `>= MinPlaceableForToggle` (1), keyed off the collection count.
  [`TripViewModel.cs:99`](../../LucidCartographer/Components/Shared/Trip/TripViewModel.cs#L99)

- [TRIP-GATE-01] auto-disable mirror — empties-to-0 is the only disable trigger.
  [`TripViewModel.cs:640`](../../LucidCartographer/Components/Shared/Trip/TripViewModel.cs#L640)

**Host wiring (the behavioural change)**

- Feed the collection count, not the viewport count, on scope/pan sync.
  [`MapPage.razor:590`](../../LucidCartographer/Components/Pages/MapPage.razor#L590)

- Same swap on the membership-change path (drives auto-disable).
  [`MapPage.razor:609`](../../LucidCartographer/Components/Pages/MapPage.razor#L609)

**Peripherals — components & tests**

- Desktop/mobile toggles both render purely off `IsToggleAvailable` (parity).
  [`TripToggle.razor:11`](../../LucidCartographer/Components/Shared/Trip/TripToggle.razor#L11)
  [`MobileTripToggle.razor:9`](../../LucidCartographer/Components/Shared/Trip/MobileTripToggle.razor#L9)

- New regression test pinning viewport-independence (the fix's whole point).
  [`MapPageViewModelTests.cs:98`](../../LucidCartographer.Tests/ViewModels/MapPageViewModelTests.cs#L98)

- Gate boundary at 1 (available) and auto-disable at 0 (empties collection).
  [`TripViewModelTests.cs:154`](../../LucidCartographer.Tests/ViewModels/TripViewModelTests.cs#L154)
  [`TripViewModelTests.cs:185`](../../LucidCartographer.Tests/ViewModels/TripViewModelTests.cs#L185)
