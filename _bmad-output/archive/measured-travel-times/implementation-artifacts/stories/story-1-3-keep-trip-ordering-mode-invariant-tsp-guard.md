# Story 1.3: Keep trip ordering mode-invariant (TSP guard)

Status: done

Adversarial review: 0 CRIT / 0 HIGH / 0 MED → SHIP. `DistanceMatrixService` already used raw `GeoUtils.HaversineDistance` and ignored `TravelTimeOptions`; the AD-1 guard now holds in code AND is pinned by a regression test that runs `SortTravelingSalesmanAsync` over a fixed 5-stop set under default vs. exaggerated (×3/×5/×8) detour factors and asserts byte-identical `OrderIndex`. An AD-1/RD3 source comment marks the haversine call so a future edit can't silently route the detour-adjusted distance through the matrix. 983 fast + 20 Trip integration green.

## Story

As a trip planner,
I want the stop ordering to stay stable and independent of the new detour factors,
So that improving estimate accuracy never silently changes my computed route order.

## Acceptance Criteria

1. **Given** the TSP cost matrix (`DistanceMatrixService`) is built from raw straight-line/haversine distance and ordering is mode-invariant (`[RD3]`), **When** the smart-haversine detour factor is introduced in the estimate path, **Then** the cost matrix continues to use **raw** haversine and is never routed through the detour-adjusted distance (AD-1 critical guard).
2. **And** the NN+2-opt ordering output is unchanged for a fixed set of stops regardless of detour-factor configuration.
3. **And** a regression test pins that `assign_stop_order`/`SetOrderAsync` results do not vary with detour-factor values.

## Architecture & Code Context

- `LucidCartographer/Services/Trip/DistanceMatrixService.cs` already builds the matrix via `GeoUtils.HaversineDistance` directly and explicitly does NOT consult `TravelTimeOptions` (the `_options` field is retained only for ctor-shape compatibility and is unused). This story confirms and PINS that invariant — the guard already holds in code; the deliverable is the regression test plus a source comment tagging the AD-1 guard so a future edit can't silently route the detour-adjusted distance through the matrix.
- Add an explicit comment near the haversine call referencing AD-1 / `[RD3]`: the cost matrix must use raw haversine and never the detour-adjusted distance, independent of `DetourFactorFor`.
- Find the ordering entry point (`SetOrderAsync` / NN+2-opt solver) that consumes `DistanceMatrix`; the regression test builds a fixed stop set, computes the order under two different detour-factor configurations, and asserts the produced order is identical.

## Constraints (NFRs)

- AD-1 critical guard — detour factor never reaches the TSP cost matrix.
- `[RD3]` — TSP cost matrix stays on raw haversine; ordering mode-invariant.

## Testing

- Regression test: for a fixed set of stops, run the ordering with detour factors at defaults vs. exaggerated values (e.g. all ×3.0) and assert the resulting stop order is byte-identical.
- Keep the Trip integration filter green.

## Build/Test commands

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast tests: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Trip integration: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`

## Dev Agent Record

The invariant already held: `DistanceMatrixService.BuildAsync` builds every cell from raw `GeoUtils.HaversineDistance` and never consults `TravelTimeOptions` (the `_options` field is retained only for ctor-shape compatibility, unused). Added an explicit AD-1/RD3 guard comment at the haversine call. Added `SortTravelingSalesman_OrderIsInvariant_ToDetourFactorConfiguration` to `TripOrderingServiceTests`: seeds a fixed non-degenerate 5-stop geometry, runs the TSP sort under default detour factors and again under exaggerated asymmetric factors (3.0/5.0/8.0) with each service wired to its own options, and asserts the resulting `OrderIndex` map is identical.

## File List

- LucidCartographer/Services/Trip/DistanceMatrixService.cs (modified — AD-1 guard comment)
- LucidCartographer.Tests/Services/TripOrderingServiceTests.cs (modified — AD-1 regression test)
