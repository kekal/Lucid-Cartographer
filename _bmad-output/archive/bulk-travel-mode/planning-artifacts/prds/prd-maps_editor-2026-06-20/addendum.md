# Addendum — Bulk Travel Mode Assignment (technical depth)

Implementation-leaning detail that supports the PRD but does not belong in the capability narrative. For architecture / dev to consume.

## A. Root cause this feature relieves

```
PoiCollectionItem.OutgoingTravelMode == null/AnyAir
  → TravelTimeComputationBackgroundService skips the leg (FR-21: only Walk/Drive/Cycle auto-compute)
  → no RouteSegment cache row for the leg's (From, To, Mode) key
  → TripLeg.Fidelity is null
  → TripViewModel.IsAnyLegComputing = OrderedLegs.Any(l => l.Fidelity is null) == true
  → TripStopList.razor: Sort + Recompute buttons disabled="@Vm.IsAnyLegComputing"
```

Note: an Any/Air leg *would* settle to `Fidelity.Placeholder` (non-null) if the provider were ever called for it (`MockTravelTimeProvider` re-badges AnyAir as Placeholder), but the background service never enqueues AnyAir legs — so in practice they stay `null` forever. This feature works around that by giving a one-action path to a ground mode; it does not change the `IsAnyLegComputing` definition.

## B. Code touchpoints

| Layer | File | Change |
|-------|------|--------|
| View | `LucidCartographer/Components/Shared/Trip/TripStopList.razor` | New header control: mode selector + overwrite toggle, beside Sort/Recompute. |
| View model | `LucidCartographer/Components/Shared/Trip/TripViewModel.cs` | New command, e.g. `SetAllLegsModeAsync(string mode, bool overwriteExisting)`, modeled on `SetLegModeAsync` (refresh projections + `travelTimeTrigger.Signal()`). |
| Ordering service | `LucidCartographer/Services/Trip/ITripOrderingService.cs`, `TripOrderingService.cs` | Reuse `SetOutgoingTravelModeAsync`; consider a batch method for one write transaction across all affected stops (atomicity + perf, supports NFR-2/NFR-5). |
| Strings | `LucidCartographer/Services/UiStrings.cs` | Selector label, toggle label, aria/title strings. |
| Mobile (deferred) | `LucidCartographer/Components/Shared/Trip/MobileTripPanel.razor` | Mirror — see PRD Open Item A2. |

## C. Leg composition (must match existing build)

The set of legs to assign must mirror `TripViewModel.BuildLegs` / `TravelTimeComputationBackgroundService.DirectionalPairs`:
- consecutive `stops[k] → stops[k+1]`, mode = `stops[k].OutgoingTravelMode`;
- on a roundtrip (no distinct finish stop): closing leg `stops[^1] → stops[0]`, mode = `stops[^1].OutgoingTravelMode`.

So "assign to all legs" means writing `OutgoingTravelMode` on every stop that is the *from*-stop of a leg — i.e. all ordered placeable stops except (for a non-roundtrip) the final stop. The writer is keyed by from-stop `PoiId`.

## D. Overwrite-toggle semantics at the data layer

- Toggle **off**: only update stops whose current `OutgoingTravelMode` is `null`/`AnyAir`.
- Toggle **on**: update every from-stop, including those with an explicit ground mode and those with a Manual `RouteSegment` row.
  - Manual override note: switching a leg's mode changes its `(From, To, Mode)` cache key. A leg that had a Manual time under its old mode will read "—" under the new mode until recomputed/re-entered. This is acceptable (explicit user action), but worth a confirm for the overwrite-on case (see PRD AC-7 protects only against *background* downgrade, not against user-initiated mode change). Flag for UX.

## E. Testing pointers

- `LucidCartographer.Tests/ViewModels/TripViewModelPerLegModeTests.cs` — extend for the bulk command (off vs on, roundtrip closing leg, no order/start/finish/budget mutation).
- `LucidCartographer.Tests/ViewModels/TripViewModelTravelModeTests.cs` — trigger/refresh behavior.
- `LucidCartographer.Tests/Components/Trip/TripStopListTests.cs` — render + visibility + disabled-state of the new control.
- Run the Trip integration filter after any DI / VM-ctor / schema change (per project convention).
