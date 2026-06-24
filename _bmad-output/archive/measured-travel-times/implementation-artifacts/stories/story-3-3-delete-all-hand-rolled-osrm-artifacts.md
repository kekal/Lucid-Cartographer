# Story 3.3: Delete all hand-rolled OSRM artifacts

Status: ready-for-dev

## Story

As an implementing developer,
I want every hand-rolled OSRM artifact removed,
So that the codebase carries no dead routing path and the build stays clean.

## Acceptance Criteria

1. Deleted: `OsrmTravelTimeProvider.cs`, `OsrmOptions.cs`, `OsrmRouteUnavailableException.cs`, the `TravelTimeSource.Osrm` constant, any residual named `"osrm"` HttpClient registration, the three `osrm-{car,foot,bike}` compose services + their commented env block, `OsrmTravelTimeProviderTests.cs`, the OSRM references in `TravelTimeComputationBackgroundServiceTests.cs`, and `docs/osrm.md` (FR-14).
2. The now-unused OSRM attribution string (`TripRoutingAttributionOsm`) is removed from `UiStrings.cs` with no dangling reference (FR-14, AD-9).
3. The solution compiles clean under `TreatWarningsAsErrors` with no group-B analyzer violations and no broken references (NFR-12).
4. The full test suite (including the Trip integration filter) passes after removal.

## Expanded scope (no-dangling-reference closure)

Epic 2 left the in-app "How to enable OSRM" surface untouched (it belongs to Epic 3's removal). To leave no dangling reference and no link to the deleted `docs/osrm.md`, also migrate:

- `TripViewModel.RecommendsOsrm` → `RecommendsMeasuredProvider`, gated on the capability seam (`ProducesMeasuredFidelity != true`) instead of the deleted `TravelTimeSource.Osrm` string.
- `UiStrings.TripMockEstimateNote` / `TripMockEstimateOsrmLink` / `TripMockEstimateOsrmHref` retargeted from OSRM/`docs/osrm.md` to Valhalla/`docs/valhalla.md` (mirrors AD-9 / FR-13: the Valhalla operator doc replaces osrm.md's role).
- `Endpoints/Docs/osrm.md` (embedded) + the `<EmbeddedResource>` in the csproj + `DocsEndpoints` `/docs/osrm.md` route → Valhalla (`/docs/valhalla.md`, embedding `Endpoints/Docs/valhalla.md`).
- The OSRM-coupled tests/comments: `TripViewModelRecommendsOsrmTests`, `OsrmDocsLinkIntegrationTests`, `TravelTimeSourceTests` Osrm-survives cases, `TripViewModelAttributionTests`, `TripMockEstimateNoteRenderTests`, and stray OSRM comments in `leafletInterop.js` / `IMapService` / `ITravelTimeProvider` / `TripProjections` / background-service tests.

## Tasks

- [x] Delete OSRM provider/options/exception source files.
- [x] Remove `TravelTimeSource.Osrm`; remove `UiStrings.TripRoutingAttributionOsm`.
- [x] Remove `osrm-{car,foot,bike}` compose services + commented OSRM app-env block.
- [x] Remove the `TravelTime:Osrm` appsettings section + its comments.
- [x] Migrate the in-app docs surface (embedded osrm.md → valhalla.md; route; csproj; DocsEndpoints; Program.cs comment).
- [x] Rename `RecommendsOsrm` → `RecommendsMeasuredProvider` (capability-gated) + retarget the Mock-estimate note copy to Valhalla.
- [x] Delete/migrate OSRM tests; fix the precision-5 leaflet decoder comment now that OSRM (the only precision-5 source) is gone.
- [x] Build clean + full suite green (incl. Trip integration filter).

## Dev Notes

- Carry-over (Epic 2 retro): the leaflet polyline decoder was globally flipped 1e-5→1e-6 with OSRM (precision-5) temporarily sharing it. Removing OSRM (the only precision-5 source) makes the 1e-6 flip unconditionally correct — update the decoder comment to drop the "OSRM mis-decodes until Epic 3" caveat.
- `Source="OSRM"` string literals that exercise the generic never-downgrade-Measured guard (not the OSRM provider) stay — they test Measured-row protection, not OSRM. Relabel comments to "a measured row" where they implied OSRM-specificity.
