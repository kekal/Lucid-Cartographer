---
baseline_commit: da4b8882dc712d36259447726126bcea11e1c153
---

# Story 2.1: Provider capability seam + Valhalla source & attribution scaffolding

Status: done

## Story

As an implementing developer,
I want the seam-level scaffolding for a measured provider in place,
So that the Valhalla provider and the recompute trigger can be built against a stable contract.

## Acceptance Criteria

1. **Given** `ITravelTimeProvider` today exposes `Source`, `Attribution`, and `GetLegAsync`, **When** I add a `bool ProducesMeasuredFidelity` member to the interface, **Then** the new member compiles into the contract with an XML doc comment explaining its purpose (capability flag gating the Epic 2 Story 2.3 recompute trigger) (AD-2).
2. **And** `MockTravelTimeProvider` implements `ProducesMeasuredFidelity` returning `false` and continues to declare `Attribution => null` unchanged (AD-2).
3. **And** `TravelTimeSource` gains `public const string Valhalla = "Valhalla"`; the existing `Osrm` constant is left **untouched** here (its removal is Epic 3) (AD-3).
4. **And** a new Valhalla ODbL routing-attribution string is added to `UiStrings.cs` (`TripRoutingAttributionValhalla`) **alongside** the existing `TripRoutingAttributionOsm` string, which stays in place until Epic 3 (AD-9, NFR8).
5. **And** the solution compiles clean under `TreatWarningsAsErrors` with no group-B analyzer violations (MA0002/MA0015/MA0046/MA0047/MA0074/VSTHRD200) (NFR-12).

## Architecture & Code Context

This is **scaffolding only** — no behaviour change, no Valhalla provider yet (that is Story 2.2), no DI wiring (Story 2.4). The goal is a stable contract the later stories build against. Every change is additive; nothing is removed (OSRM artifacts persist through Epic 2 and are deleted in Epic 3).

### `LucidCartographer/Services/Trip/ITravelTimeProvider.cs` (UPDATE)

- The interface currently declares `string Source`, `string? Attribution`, and `Task<TravelLegResult> GetLegAsync(...)`.
- Add a new member: `bool ProducesMeasuredFidelity { get; }`.
- Add an XML `<summary>` doc comment in the established style of the file (the existing `Source`/`Attribution` members are richly documented — match that tone). Explain: this is a **capability flag**. `true` means the provider returns real road-network measurements (Fidelity `Measured`); `false` means estimate-only (Fidelity `Estimated`/`Placeholder`). Note that the Epic 2 Story 2.3 background recompute trigger reads it to decide whether an existing Estimated/fallback cache row is *upgrade-eligible* — a Mock-only deployment must NOT re-churn its own estimates (AD-2), so the broadened pending-leg arm is gated on this being `true`.
- Adding a member to an interface forces every implementer to implement it. Today the only production implementer is `MockTravelTimeProvider`. There may be test doubles/fakes that implement `ITravelTimeProvider` — search the test project for `: ITravelTimeProvider` and update each to return a sensible value (`false` unless the fake deliberately models a measured provider). This is the single most likely build-break; do not miss it.

### `LucidCartographer/Services/Trip/MockTravelTimeProvider.cs` (UPDATE)

- Sealed class, primary constructor `(IOptions<TravelTimeOptions> options)`.
- Add `public bool ProducesMeasuredFidelity => false;` — the haversine mock is estimate-only.
- Leave everything else exactly as-is: `Source => ProviderId` (`"Mock"`), `Attribution => null`, and `GetLegAsync` unchanged. AC 2 explicitly requires `Attribution` to stay `null`.

### `LucidCartographer/Services/Trip/TravelTimeSource.cs` (UPDATE)

- Static class of provenance string constants stamped onto `RouteSegment.Source`. Current members: `Mock = "Mock"`, `Manual = "Manual"`, `Osrm = "OSRM"`, `EstimatedFallback = "EstimatedFallback"`.
- Add `public const string Valhalla = "Valhalla";` with an XML doc comment (mirror the existing `Osrm` comment style): self-hosted Valhalla provider — measured leg from real road network with encoded geometry; opt-in per deployment, never the default.
- **Do NOT touch the `Osrm` constant.** It stays until Epic 3 Story 3.3. Removing or renaming it here breaks the still-present OSRM provider/tests/attribution wiring and is out of scope.

### `LucidCartographer/Services/UiStrings.cs` (UPDATE)

- Find the existing `TripRoutingAttributionOsm` constant (under the `// Trip View — routing-data attribution` comment). Its current value:
  `"Routing &copy; OSRM &middot; Map data &copy; OpenStreetMap contributors (ODbL)"`.
- Add a sibling `TripRoutingAttributionValhalla` constant **next to it**, e.g.:
  `"Routing &copy; Valhalla &middot; Map data &copy; OpenStreetMap contributors (ODbL)"`.
- Match the existing HTML-entity escaping convention exactly (`&copy;`, `&middot;`) — this string is rendered into the map attribution control as HTML, so it must use entities, not raw `©`/`·`. Mirror the casing/spacing of the OSRM string precisely so the two read identically apart from the provider name.
- **Keep `TripRoutingAttributionOsm`** — it is still referenced by the OSRM path through Epic 2. Its removal is Epic 3 Story 3.3 (which deletes it "with no dangling reference"). The new Valhalla string is not yet wired to anything in this story; Story 2.2 returns it from `ValhallaTravelTimeProvider.Attribution` and Story 2.4 surfaces it on the map.

## Constraints (NFRs)

- **NFR-12 — Build discipline.** Must compile clean under `TreatWarningsAsErrors` + analyzer regime; no new group-B analyzer violations (MA0002/MA0015/MA0046/MA0047/MA0074/VSTHRD200). Public members need XML doc comments to satisfy the doc-comment analyzer.
- **NFR8 — Attribution.** The Valhalla string is the future routing attribution for the measured provider; it is added now but only wired up in later stories.
- **AD-2 / AD-3 / AD-9** — interface capability flag, source constant, attribution string. This story lays down all three seam touchpoints so Stories 2.2–2.4 have a stable contract.
- **Additive-only / no regression.** OSRM artifacts and the `Osrm` source constant and `TripRoutingAttributionOsm` string remain present and functional. Do not delete, rename, or rewire anything.

## Testing

- **Interface/Mock:** `MockTravelTimeProviderTests` (in `LucidCartographer.Tests/Services/MockTravelTimeProviderTests.cs`) already covers `Attribution` is null and the per-mode estimate behaviour. Add a small assertion that `ProducesMeasuredFidelity` is `false` for the Mock (mirror the existing `Attribution_IsNull_*` fact style).
- **Source constant:** a trivial test (or extend an existing TravelTimeSource test if one exists) asserting `TravelTimeSource.Valhalla == "Valhalla"` and that `TravelTimeSource.Osrm` is still `"OSRM"` (proving the OSRM constant survived).
- **UiStrings:** optional — assert `UiStrings.TripRoutingAttributionValhalla` is non-empty and contains `"Valhalla"` and `"ODbL"`, and that `TripRoutingAttributionOsm` is unchanged.
- Run the fast suite; this is a non-behavioural scaffolding change so the full suite (incl. the Trip integration filter) must stay green.

## Build/Test commands

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast tests: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Trip integration: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`

## Dev Notes

- This story unblocks the rest of Epic 2: Story 2.2 (`ValhallaTravelTimeProvider`) returns `ProducesMeasuredFidelity => true` and `Attribution => UiStrings.TripRoutingAttributionValhalla` and stamps `Source = TravelTimeSource.Valhalla`; Story 2.3's recompute trigger gates on `ProducesMeasuredFidelity`.
- Epic 1 (smart-haversine) is complete (3/3 stories done, retrospective done); the Mock/`EstimatedTravelTime.Compute` estimate path is the established degrade target — do not alter it here.
- Watch the interface-member-added build break: any `: ITravelTimeProvider` implementer in the test project must gain the new member. Search before building.

### References

- [Source: _bmad-output/planning-artifacts/epics.md#Story 2.1] — acceptance criteria
- [Source: _bmad-output/planning-artifacts/epics.md#Additional Requirements] — AD-2 (capability flag), AD-3 (Valhalla source/contract), AD-9 (attribution string)
- [Source: LucidCartographer/Services/Trip/ITravelTimeProvider.cs] — interface being extended
- [Source: LucidCartographer/Services/Trip/MockTravelTimeProvider.cs] — implementer to update
- [Source: LucidCartographer/Services/Trip/TravelTimeSource.cs] — source constants
- [Source: LucidCartographer/Services/UiStrings.cs#Trip View — routing-data attribution] — attribution strings

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (BMAD dev-story workflow)

### Debug Log References

- A new test file initially declared `namespace LucidCartographer.Tests.Services`, which shadowed `LucidCartographer.Services.*` (e.g. `UiStrings`, `Browser`, `SqliteWriteLock`) for sibling files that `using LucidCartographer.Services;`. Existing test files use the flat `namespace LucidCartographer.Tests` regardless of folder. Renamed the new file's namespace to `LucidCartographer.Tests`; build went clean.

### Completion Notes List

- AC 1: Added `bool ProducesMeasuredFidelity { get; }` to `ITravelTimeProvider` with an XML doc comment describing it as the Epic 2 Story 2.3 capability gate (AD-2).
- AC 2: `MockTravelTimeProvider.ProducesMeasuredFidelity => false`; `Attribution => null` and all other members left unchanged.
- AC 3: Added `TravelTimeSource.Valhalla = "Valhalla"` with doc comment; `Osrm = "OSRM"` left untouched.
- AC 4: Added `UiStrings.TripRoutingAttributionValhalla` next to the unchanged `TripRoutingAttributionOsm`, matching HTML-entity escaping (`&copy;`/`&middot;`) and ODbL wording.
- AC 5: Build succeeds under `TreatWarningsAsErrors` with 0 warnings / 0 errors; no group-B analyzer violations.
- Interface-member break handled: updated the production `OsrmTravelTimeProvider` (`=> true`, measured) plus 5 test doubles — `FakeProvider`, `MeasuredProvider` (`=> true`), `CountingProvider`, `StubProvider`, `ThrowingProvider`, and `ThrowOnFirstLegProvider` (delegates to inner Mock).
- Tests added: `ProducesMeasuredFidelity_IsFalse_HaversineIsEstimateOnly` in `MockTravelTimeProviderTests`; new `TravelTimeSourceTests` covering Valhalla/Osrm constants and Valhalla/Osm attribution strings.
- Results: Build clean (0 warnings). Fast suite 988 passed / 0 failed. Trip integration filter 20 passed / 0 failed.

### File List

- LucidCartographer/Services/Trip/ITravelTimeProvider.cs (modified)
- LucidCartographer/Services/Trip/MockTravelTimeProvider.cs (modified)
- LucidCartographer/Services/Trip/OsrmTravelTimeProvider.cs (modified)
- LucidCartographer/Services/Trip/TravelTimeSource.cs (modified)
- LucidCartographer/Services/UiStrings.cs (modified)
- LucidCartographer.Tests/Services/MockTravelTimeProviderTests.cs (modified)
- LucidCartographer.Tests/Services/TravelTimeSourceTests.cs (new)
- LucidCartographer.Tests/ViewModels/TripViewModelAttributionTests.cs (modified)
- LucidCartographer.Tests/ViewModels/TripViewModelRecomputeTests.cs (modified)
- LucidCartographer.Tests/ViewModels/TripViewModelRecommendsOsrmTests.cs (modified)
- LucidCartographer.Tests/Services/TravelTimeComputationBackgroundServiceTests.cs (modified)

## Senior Developer Review (AI)

- Reviewer: satec\yurik — 2026-06-24 (story-automator-review, cycle 1)
- Outcome: **Approve** — 0 CRITICAL, 0 HIGH, 0 MEDIUM, 0 LOW.
- AC 1 ✅ `ITravelTimeProvider.ProducesMeasuredFidelity` added with a rich XML doc comment naming the Story 2.3 recompute gate (AD-2).
- AC 2 ✅ `MockTravelTimeProvider.ProducesMeasuredFidelity => false`; `Attribution => null` and `GetLegAsync` unchanged.
- AC 3 ✅ `TravelTimeSource.Valhalla = "Valhalla"` added with doc comment; `Osrm = "OSRM"` untouched.
- AC 4 ✅ `UiStrings.TripRoutingAttributionValhalla` added next to the unchanged `TripRoutingAttributionOsm`, matching `&copy;`/`&middot;` escaping and ODbL wording.
- AC 5 ✅ Build clean under TreatWarningsAsErrors (0 warnings / 0 errors); no group-B analyzer violations.
- Interface break handled correctly: `OsrmTravelTimeProvider` returns `true`; the new `TravelTimeSourceTests` discriminates Mock(false)/OSRM(true) through the interface type, locking the seam Story 2.3 reads.
- Verification: `dotnet build` clean; fast suite 989 passed / 0 failed; Trip integration filter 20 passed / 0 failed.
- Note (informational, out of scope): the working tree carries unrelated pre-existing uncommitted changes (EstimatedTravelTime, DistanceMatrixService, TravelTimeOptions, appsettings.json, etc.) from earlier Epic work; confirmed they contain no Valhalla/ProducesMeasuredFidelity content and are not part of story 2.1's review surface.

## Change Log

- 2026-06-24: Senior Developer Review (AI) — Approve, 0 issues across all severities. Build clean; 989 fast + 20 Trip integration tests green. Status review → done.
- 2026-06-24: Story 2.1 implemented — added `ITravelTimeProvider.ProducesMeasuredFidelity` capability seam, `TravelTimeSource.Valhalla` constant, and `UiStrings.TripRoutingAttributionValhalla` attribution string (all additive scaffolding for Epic 2). Updated all interface implementers (OSRM provider + 5 test doubles). Added Mock capability-flag test and `TravelTimeSourceTests`. Build clean under TreatWarningsAsErrors; 988 fast tests + 20 Trip integration tests green. Status → review.
