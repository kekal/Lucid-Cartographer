---
title: 'Bulk Travel Mode Assignment (TRIP-BULKMODE-01)'
type: 'feature'
created: '2026-06-20'
status: 'done'
baseline_commit: 'a6114bdeafc5babb7f29f6a5d148651f57484639'
context:
  - '{project-root}/_bmad-output/planning-artifacts/prds/prd-maps_editor-2026-06-20/prd.md'
  - '{project-root}/_bmad-output/planning-artifacts/prds/prd-maps_editor-2026-06-20/addendum.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Trip legs default to Any/Air, which never auto-computes, so `IsAnyLegComputing` stays true and the **Sort** + **Recompute** buttons are disabled until the planner sets a mode on every leg one at a time. For an N-stop trip that is N separate actions before anything computes.

**Approach:** Add a single header control in the Trip stops panel that assigns a travel mode (Drive / Walk / Cycle / Any/Air) to **all** legs of the active trip at once, with a checkbox choosing whether to overwrite legs that already carry an explicit mode (default: off = fill only Any/Air legs). Writes flow through the existing per-leg-mode write path and trigger the existing background recompute.

## Boundaries & Constraints

**Always:**
- All persistence goes through `ITripOrderingService` (the sole writer of `OutgoingTravelMode`); the Razor component raises ONE VM command only and never touches services/DB (NFR1).
- "All legs" mirrors `BuildLegs` / `DirectionalPairs`: every from-stop of a leg — consecutive stops `0..N-2`, plus the last stop (closing leg) on a Roundtrip with no distinct Finish.
- A bulk assignment changes only `OutgoingTravelMode`. It must NOT alter Stop Order, Start/Finish, or the time budget.
- Bulk persistence is ONE write transaction (no per-leg round-trip) followed by ONE projection refresh + ONE `Notify()` (NFR-2/NFR-5); a ground mode signals `travelTimeTrigger` exactly as `SetLegModeAsync` does today.
- The control is shown only when `Vm.OrderedLegs.Count > 0`.
- The control must NOT gate on `Vm.IsAnyLegComputing` (FR-13, amended): it is the remedy for uncomputed / Any-Air legs, not a consumer of settled times — gating on `IsAnyLegComputing` would disable it exactly when an all-Any/Air trip needs it. It is disabled only transiently while its own bulk request is in flight (anti-double-submit).
- All copy via `UiStrings` (Trip*-prefixed); selector + checkbox expose aria-labels.

**Ask First:**
- Adding a confirm prompt before an overwrite-on assignment that would clear Manual times (PRD open item A6 — default for now: NO confirm). Do not add one unless the human asks.

**Never:**
- Do not mirror into `MobileTripPanel.razor` (deferred to tech-debt, PRD A2).
- Do not change the definition of `IsAnyLegComputing`.
- Do not add new travel modes/providers.
- Do not add subset/partial-leg selection (per-leg pill already covers that).
- Do not touch the legacy collection-wide `PoiCollection.TravelMode` / `SetTravelModeAsync` path.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Fill-empty (overwrite off) | All legs Any/Air; pick Drive | Every from-stop set to Drive; trigger signalled; after compute, Sort/Recompute enable | N/A |
| Fill-empty preserves manual modes | Some legs Walk, rest Any/Air; overwrite off; pick Drive | Only Any/Air legs become Drive; Walk legs unchanged | N/A |
| Overwrite on | Some legs Walk; overwrite on; pick Drive | Every from-stop (incl. Walk) becomes Drive | N/A |
| Bulk Any/Air revert | Any legs; pick Any/Air | All from-stops → AnyAir; times read "—"; Sort/Recompute disable; existing ground cache rows untouched (just unreferenced) | N/A |
| Roundtrip closing leg | Roundtrip, no distinct Finish; pick Drive | Last stop's `OutgoingTravelMode` also set (closing leg) | N/A |
| Open path (distinct Finish) | Finish set; pick Drive | Final stop is not a from-stop; its mode is not required for any leg | N/A |
| Invalid mode | mode not in `TravelMode.All` and not null | Service throws `ArgumentException` | Surface via existing VM try/catch + log; no partial write |
| No active trip / Trip View off | command raised with no active collection | No-op | N/A |

</frozen-after-approval>

## Code Map

- `LucidCartographer/Services/Trip/ITripOrderingService.cs` -- add `SetAllOutgoingTravelModesAsync(int collectionId, string? mode, bool overwriteExisting, CancellationToken)` (batch sibling of `SetOutgoingTravelModeAsync`).
- `LucidCartographer/Services/Trip/TripOrderingService.cs` -- implement the batch method: load ordered placeable stops + Start/Finish, compute from-stops, set `OutgoingTravelMode` (all, or only null/AnyAir when `overwriteExisting` false) in one `SaveChanges` under `SqliteWriteLock`; validate `mode`.
- `LucidCartographer/Components/Shared/Trip/TripViewModel.cs` -- add `SetAllLegsModeAsync(string mode, bool overwriteExisting)` mirroring `SetLegModeAsync` (guard active+TripView; call service; `RefreshProjectionsAsync`; signal trigger iff ground mode; `Notify`).
- `LucidCartographer/Components/Shared/Trip/BulkLegModeSelector.razor` -- NEW presentational control: mode dropdown (Walk/Drive/Cycle/Any-Air) + overwrite checkbox; raises `Vm.SetAllLegsModeAsync`; disabled on `Vm.IsAnyLegComputing`. Mirror `LegModePill.razor` conventions (glyphs, UiStrings, stopPropagation, popover).
- `LucidCartographer/Components/Shared/Trip/TripStopList.razor` -- render `<BulkLegModeSelector Vm="Vm" />` in the header action row beside Sort/Recompute (inside the `OrderedLegs.Count > 0` block).
- `LucidCartographer/Services/UiStrings.cs` -- add bulk selector label/aria + overwrite-checkbox label/aria (reuse existing `TripTravelMode*` mode names).
- `LucidCartographer.Tests/ViewModels/TripViewModelPerLegModeTests.cs` -- VM bulk-command tests.
- `LucidCartographer.Tests/Components/Trip/TripStopListTests.cs` (or new `BulkLegModeSelectorTests.cs`) -- render/visibility/disabled tests.

## Tasks & Acceptance

**Execution:**
- [x] `ITripOrderingService.cs` + `TripOrderingService.cs` -- add & implement `SetAllOutgoingTravelModesAsync`; from-stop selection mirrors `DirectionalPairs`; validate mode; single gated transaction; no Stop Order / Start / Finish mutation.
- [x] `TripViewModel.cs` -- add `SetAllLegsModeAsync(mode, overwriteExisting)`; same guard/refresh/trigger/notify shape as `SetLegModeAsync`; one refresh + one Notify.
- [x] `BulkLegModeSelector.razor` -- new presentational control (mode menu + overwrite checkbox); always enabled when legs present, disabled only while its own bulk request is in flight (NOT on `IsAnyLegComputing`); all copy via UiStrings.
- [x] `TripStopList.razor` -- mount the control in the header action row (legs-present block).
- [x] `UiStrings.cs` -- add the new Trip*-prefixed strings.
- [x] `TripViewModelBulkModeTests.cs` (new) -- covers the I/O matrix: fill-empty, preserve-manual, overwrite-on, Any/Air revert, roundtrip closing leg, open path, no-mutation invariants, invalid mode throws.
- [x] `BulkLegModeSelectorTests.cs` (new) -- visibility (legs>0), enabled even when `IsAnyLegComputing` is true (all-Any/Air trip), and selecting persists the mode (overwrite off + on).

**Acceptance Criteria:**
- Given an all-Any/Air trip, when the planner picks Drive (overwrite off), then every leg becomes Drive and — after background compute settles — Sort and Recompute become enabled.
- Given a trip with some Walk legs, when picking Drive with overwrite **off**, then only the Any/Air legs change and the Walk legs stay Walk; with overwrite **on**, all legs become Drive.
- Given any trip, when picking Any/Air in bulk, then all legs revert to Any/Air, times show "—", and Sort/Recompute disable.
- Given a Roundtrip, when a bulk mode is applied, then the closing leg (last stop's outgoing mode) is also set.
- Given any bulk assignment, when it completes, then Stop Order, Start/Finish, and time budget are unchanged.
- Given an all-Any/Air trip (`IsAnyLegComputing` true), when the planner opens the control, then it is enabled and usable (it is NOT gated on compute state).

## Spec Change Log

- **2026-06-20 — FR-13 reversed (human-approved frozen amendment).** Finding (during step-03 impl): gating the control on `IsAnyLegComputing` is self-defeating — an Any/Air leg has null Fidelity ⇒ `IsAnyLegComputing` is true ⇒ the control would be disabled on exactly the all-Any/Air trips it exists to fix, and overwrite-off (which only touches Any/Air legs) could never fire. Amended: the control is always enabled when `OrderedLegs > 0`, disabled only transiently while its own bulk request is in flight (anti-double-submit). Avoids shipping a permanently-disabled-when-needed control. KEEP: control still hides when there are no legs; the per-leg `SetLegModeAsync` trigger/refresh shape is unchanged.

## Design Notes

Root cause and the exact leg/cache mechanics are in PRD `addendum.md` §A/§C/§D — read it for the from-stop rule and the Manual-time edge. The batch service method exists for NFR-5 (one transaction); do NOT loop `SetOutgoingTravelModeAsync` per stop. Overwrite-on can blank a Manual time under the old mode key — that is accepted (explicit user action); no confirm unless A6 is reopened.

## Verification

**Commands:**
- `dotnet build LucidCartographer/LucidCartographer.csproj` -- expected: builds clean.
- `dotnet test LucidCartographer.Tests --filter "FullyQualifiedName~Trip"` -- expected: all Trip tests pass (incl. new bulk tests + integration filter per project convention).

## Suggested Review Order

**The control & its behavior (start here)**

- Entry point — the whole presentational control: mode menu + overwrite checkbox, NOT gated on compute.
  [`BulkLegModeSelector.razor:23`](../../LucidCartographer/Components/Shared/Trip/BulkLegModeSelector.razor#L23)
- The only-while-in-flight disable + non-sticky overwrite reset (review-patched).
  [`BulkLegModeSelector.razor:95`](../../LucidCartographer/Components/Shared/Trip/BulkLegModeSelector.razor#L95)

**The bulk write (architecturally interesting)**

- From-stop selection mirrors DirectionalPairs; single gated transaction; validate-first.
  [`TripOrderingService.cs:294`](../../LucidCartographer/Services/Trip/TripOrderingService.cs#L294)
- VM command: guard → service → refresh → trigger (ground only) → one Notify.
  [`TripViewModel.cs:1460`](../../LucidCartographer/Components/Shared/Trip/TripViewModel.cs#L1460)
- Interface contract for the batch writer.
  [`ITripOrderingService.cs:194`](../../LucidCartographer/Services/Trip/ITripOrderingService.cs#L194)

**Wiring & copy**

- Mount point in the header action row (legs-present block).
  [`TripStopList.razor:50`](../../LucidCartographer/Components/Shared/Trip/TripStopList.razor#L50)
- New UiStrings constants (label, aria, overwrite).
  [`UiStrings.cs:261`](../../LucidCartographer/Services/UiStrings.cs#L261)

**Tests (peripherals)**

- VM/service matrix: fill-empty, preserve, overwrite, revert, open-path, no-mutation, manual-row safety, invalid mode.
  [`TripViewModelBulkModeTests.cs:1`](../../LucidCartographer.Tests/ViewModels/TripViewModelBulkModeTests.cs#L1)
- Component: enabled when all-Any/Air, persists on pick, overwrite checkbox, header mount.
  [`BulkLegModeSelectorTests.cs:1`](../../LucidCartographer.Tests/Components/Trip/BulkLegModeSelectorTests.cs#L1)
