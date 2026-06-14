---
baseline_commit: 8b6e166
---

# Story 4.2: Draw road-shaped legs and OSM attribution

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a trip planner with OSRM enabled,
I want my Drive/Walk/Cycle legs drawn along real roads,
so that the map shows the true shape of the route, honestly badged.

## Acceptance Criteria

_(FR-6, AR-7, NFR8, UX-DR4, UX-DR9, UX-DR12; epics.md#Story-4.2)_

1. **Road geometry ⇒ solid; no geometry ⇒ dashed connector.** Given a leg for which the active provider returned road geometry (a non-empty `RouteSegment.GeometryPolyline`, i.e. **Measured**), when the map renders it, then the line **follows the roads** (the decoded polyline vertices) and renders **solid, full-weight, `primary`** — the only solid state. Given a leg with **no** geometry (null/empty `GeometryPolyline` — Estimated / Manual / Placeholder / Air, or a Measured leg that somehow lacks geometry), when it renders, then it draws a **straight connector, dashed + muted** (unchanged Phase-1 behaviour).
2. **Air stays dashed.** Given an Any/Air leg, when it renders, then it remains a **dashed, muted** connector (no road geometry is ever fetched for Air — Story 4.1 guarantees Air carries `GeometryPolyline == null`). The closing Roundtrip leg follows the same solidity rule as any other leg (solid only if it has geometry).
3. **Estimated→Measured upgrade redraws live (UX-DR9).** Given an Estimated leg on an open trip when OSRM becomes available, when a recompute / provider-available signal fills the cache row with Measured geometry, then the leg **upgrades to Measured on screen** — its line goes from dashed-straight to solid-road-shaped and its time badge updates — landing via `StateChanged` (the existing progress→`RefreshLegsFromCache`→`Notify`→`PushTripLegsAsync` path), **never silently on a stale screen**.
4. **OSM/ODbL attribution when an OSM-based provider is active (NFR8).** Given an **OSM-based** travel-time provider is active (OSRM), when the map renders on **either** surface (desktop and `Mobile*Screen`), then OSM/ODbL attribution for the **routing data** is visible on the map (in addition to the existing OSM tile attribution). Given the default **Mock** provider (not OSM-based), no routing attribution is added (the base OSM **tile** attribution remains as today). All new copy goes through `UiStrings` (NFR5).
5. **One consistent geometry encoding.** Geometry is decoded with the **same encoding/precision Story 4.1 produced** (encoded polyline, **precision 5**). `GeometryPolyline == null/empty` ⇒ no road geometry ⇒ dashed/muted render. No second encoding is introduced.
6. **Dual-surface + dark mode, incremental redraw.** Every behaviour above lands on **both** desktop and mobile (they already share `LeafletMap.razor` + `drawTripLegs`); solid/`primary` and dashed/muted both honour the dark-mode token palette (`--primary`, `--trip-leg-muted`); the redraw on an order/upgrade change stays **incremental** (replace only the trip-leg layer, no map re-init), and legs never intercept marker clicks (`interactive: false` preserved).

## Tasks / Subtasks

- [x] **Task 1 — Project `GeometryPolyline` up to the leg projection** (AC: #1, #3, #5)
  - [x] `Components/Shared/Trip/TripProjections.cs` — add `string? GeometryPolyline = null` to the `TripLeg` record (XML doc: encoded polyline, precision 5; null = no road geometry = dashed/muted; only Measured legs carry it).
  - [x] `Components/Shared/Trip/TripViewModel.cs` `MakeLeg` — populate it from `seg?.GeometryPolyline` (it is already null for non-Measured rows; OSRM writes it for Measured). No other behaviour change.
- [x] **Task 2 — Carry geometry through the DTO and the redraw dedup** (AC: #1, #3, #6)
  - [x] `Services/LeafletMapService.cs` — add `string? GeometryPolyline` to `TripLegDto` (camelCase `geometryPolyline` to JS). Update the XML doc.
  - [x] `Components/Pages/MapPage.razor` `PushTripLegsAsync` — project `l.GeometryPolyline` into the DTO.
  - [x] **Dedup correctness:** ensure `LegsEqual` (the no-redraw guard) compares **`GeometryPolyline` too** (and/or `IsMeasured`), so an Estimated→Measured upgrade that keeps the same endpoints but gains geometry is **not** swallowed by the dedup (AC3). Verify `IsMeasured` already flips on upgrade — if `LegsEqual` only compares coordinates, the redraw would be skipped; add geometry/measured to the comparison.
- [x] **Task 3 — Decode the polyline + draw road-shaped solid legs in JS** (AC: #1, #2, #5, #6)
  - [x] `wwwroot/js/leafletInterop.js` `drawTripLegs` — for each leg compute `hasGeometry = !!leg.geometryPolyline`. If `hasGeometry`, **decode** the precision-5 encoded polyline to a `[[lat,lon],…]` array and build the `L.polyline` from those vertices with the **Measured** style (the existing `measured` branch: `className 'trip-leg-line trip-leg-measured'`, `dashArray: null`, `weight: 4`, `opacity: 1`); else fall back to the existing straight `[[fromLat,fromLon],[toLat,toLon]]` dashed-muted connector. **Solidity keys off geometry presence**, not `isMeasured` alone (AC1) — a Measured-but-geometryless leg must still draw dashed.
  - [x] Add a small, self-contained **encoded-polyline decoder** (the standard Google/OSRM precision-5 algorithm, ~15 lines) inside `leafletInterop.js` — **no new bundled library, no CDN** (ARCH-HIGH-07: Leaflet is self-hosted; keep dependencies vendored/inline). Guard against a malformed string (decode failure ⇒ fall back to the straight connector, never throw).
  - [x] Keep `interactive: false` and the `trip-leg-closing` tagging exactly as today. Bump the `leafletInterop.js?v=` query in `Components/App.razor`.
  - [x] Confirm the CSS already covers it: `.trip-leg-measured { stroke: var(--primary…) }` (solid via `dashArray:null`) and `.trip-leg-line { stroke: var(--trip-leg-muted) }` — both already token-driven + dark-mode aware (`base.css`). No new hardcoded hex.
- [x] **Task 4 — OSM/ODbL routing attribution when OSRM is active** (AC: #4)
  - [x] Surface "is an **OSM-based** provider active + its attribution" to the UI. **Add `string? Attribution { get; }` to `ITravelTimeProvider`** (the provider knows its own data licence): `MockTravelTimeProvider` ⇒ `null` (no routing attribution — haversine isn't OSM-derived); `OsrmTravelTimeProvider` ⇒ the OSM/ODbL routing attribution HTML (via `UiStrings`). This is the clean seam — no UI→config sniffing.
  - [x] Add the attribution copy to `Services/UiStrings.cs` (e.g. `TripRoutingAttributionOsm` — factual, e.g. `"Routing © OSRM · Map data © OpenStreetMap contributors (ODbL)"`; UX-DR11 voice). No hype.
  - [x] Push it to the map: a new `IMapService.SetRoutingAttributionAsync(string? html)` → `leafletInterop.setRoutingAttribution(html)` that adds the routing attribution to Leaflet's attribution control (`map.attributionControl.addAttribution(html)`), or removes the prior one when `html` is null. Call it once from `MapPage` after the map initialises (both desktop and mobile share the one `LeafletMap`, so one call covers both surfaces). The page reads the active provider's `Attribution` (inject `ITravelTimeProvider`, or expose it via `TripViewModel` to keep the page off the service layer — prefer surfacing a `RoutingAttributionHtml` string on the VM).
  - [x] The base **tile** attribution (`'&copy; OpenStreetMap contributors'` in `initMap`) stays unchanged — this task adds the **routing** attribution on top, only when OSM-based routing is active.
- [x] **Task 5 — Tests** (AC: all)
  - [x] **Projection unit (`TripViewModel`/`TripProjections`):** a Measured `RouteSegment` with a `GeometryPolyline` ⇒ the built `TripLeg.GeometryPolyline` carries it and `IsMeasured == true`; an Estimated/Placeholder/Manual row ⇒ `GeometryPolyline == null`. An Any/Air leg ⇒ null geometry (dashed).
  - [x] **DTO flow / StubMapService:** extend `Integration/StubMapService.cs` to record the last legs' geometry (e.g. `LastTripLegGeometries: List<string?>`); assert in an integration test that a Measured leg pushes its polyline string and a non-Measured leg pushes null/empty — and that an Estimated→Measured cache fill triggers a **re-push** (AC3 dedup: the upgrade is not swallowed).
  - [x] **Attribution:** unit-assert `OsrmTravelTimeProvider.Attribution` is the OSM/ODbL `UiStrings` value and `MockTravelTimeProvider.Attribution` is null; (optional) a Stub/record assertion that `SetRoutingAttributionAsync` is called with the OSM html when OSRM is the active provider and with null under Mock.
  - [x] **Trip integration filter** — run after the DI/projection changes (A3 standing gate — no VM-ctor change expected unless you inject `ITravelTimeProvider` into the VM; if you do, this is exactly the integration-host regression point: confirm the parameterless `AddTripServices()` host still constructs the VM, i.e. the provider it needs is available in that overload **or** the VM takes it optionally). **Decide deliberately:** the parameterless overload registers **no** `ITravelTimeProvider`, so if `TripViewModel` takes `ITravelTimeProvider` as a ctor dependency the integration host will fail to construct it — either (a) surface attribution without a VM ctor dependency (e.g. inject into the page, or register a no-op provider-info in the parameterless overload), or (b) register a default provider-info in the parameterless overload. Do **not** add `ITravelTimeProvider` to the VM ctor without covering the host.
  - [x] **JS-render harness limit:** the decode→solid-road rendering and the attribution-control DOM are JS-only and not exercised by the C# integration harness (`StubMapService` no-ops Leaflet, mirroring the 1-2/1-4 map-marker defers). Verify by inspection; record the harness limitation in `deferred-work.md` and (optionally) note a future Playwright assertion. Do not fake a JS assertion in C#.
- [x] **Task 6 — Build & full-suite verification**
  - [x] `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug` → clean (0 warnings; no group-B analyzer violations; no `ConfigureAwait(false)`; no new `MA0026`; no hardcoded UI string — attribution via `UiStrings`).
  - [x] Fast unit/component pass, then full suite incl. Trip integration. Record counts in Debug Log.

## Dev Notes

### What already exists (Phase-1 scaffolding — do NOT rebuild it)

Story 1.3 deliberately left the Measured path stubbed; most of the wiring is already here:
- **`leafletInterop.js` `drawTripLegs`** already has a `measured` branch (`className 'trip-leg-line trip-leg-measured'`, `dashArray: measured ? null : '6 6'`, `weight: measured ? 4 : 2`, `opacity: measured ? 1 : 0.7`, `interactive:false`, `trip-leg-closing` tagging, incremental layer replace). **It currently always draws the straight `[from,to]` line** even on the (never-taken) measured branch. Your job: feed **decoded geometry** into the measured branch and gate "solid" on geometry presence.
- **`base.css`** already defines `.trip-leg-line { stroke: var(--trip-leg-muted) }` and `.trip-leg-measured { stroke: var(--primary,#005bbf) }`, both dark-mode-aware (`--trip-leg-muted` flips under `html[data-theme="dark"]`). **No CSS change needed** for solidity/colour — applying the `trip-leg-measured` class + `dashArray:null` already yields solid + primary.
- **`TripLeg.IsMeasured`** is already derived (`Fidelity == Measured`) in `MakeLeg`; **`TripLegDto.IsMeasured`** is already sent to JS. The gap is purely the **geometry string** (not projected today) — `TripLeg`/`TripLegDto` carry no geometry yet.
- **The live-redraw path is already built (AC3):** `EnsureProgressSubscription` → `RefreshLegsFromCacheAsync` (re-reads the cache, rebuilds `OrderedLegs`, `Notify()`) → `MapPage.OnTripChanged` → `PushTripLegsAsync` (with `LegsEqual` dedup). When a cache row upgrades to Measured-with-geometry, this fires automatically — you only need (a) the geometry in the projection and (b) `LegsEqual` to not swallow the upgrade.

### The two real new pieces

1. **Geometry string threaded end-to-end:** `RouteSegment.GeometryPolyline` → `TripLeg.GeometryPolyline` (`MakeLeg`) → `TripLegDto.GeometryPolyline` (`PushTripLegsAsync`) → `drawTripLegs(leg.geometryPolyline)`.
2. **A precision-5 encoded-polyline decoder in JS** (none is bundled; ARCH-HIGH-07 forbids a CDN). Inline the standard ~15-line algorithm. **Must match Story 4.1's `geometries=polyline` precision 5** (`OsrmOptions.GeometryPrecision` default 5). If 4.1's precision ever changes to 6, the decoder's precision must change with it — keep them in lockstep (call it out in a comment referencing TRIP-OSRM-01).

### Key source files

- `LucidCartographer/wwwroot/js/leafletInterop.js` — `drawTripLegs` (the `measured` branch to feed geometry into), `initMap` (the tile attribution; add the routing-attribution helper near it). Bump `?v=` in `App.razor`.
- `LucidCartographer/Components/App.razor` — the `<script src="js/leafletInterop.js?v=17">` version bump.
- `LucidCartographer/Services/LeafletMapService.cs` + `IMapService.cs` — `TripLegDto` (+`GeometryPolyline`), `DrawTripLegsAsync`, and a new `SetRoutingAttributionAsync`. `Integration/StubMapService.cs` implements `IMapService` — extend it to record geometry + the attribution call.
- `LucidCartographer/Components/Pages/MapPage.razor` — `PushTripLegsAsync` (project geometry; fix `LegsEqual`), and the one-time `SetRoutingAttributionAsync` call after map init.
- `LucidCartographer/Components/Shared/Trip/TripProjections.cs` (`TripLeg`), `TripViewModel.cs` (`MakeLeg`; possibly a `RoutingAttributionHtml` surface).
- `LucidCartographer/Services/Trip/ITravelTimeProvider.cs`, `MockTravelTimeProvider.cs`, `OsrmTravelTimeProvider.cs` — add `Attribution`.
- `LucidCartographer/Services/UiStrings.cs` — the OSM/ODbL routing-attribution string.
- `LucidCartographer/wwwroot/css/base.css` — confirm only; no change expected.

### Decisions / scope guards

- **Solidity = geometry presence (not `isMeasured` alone), per UX-DR4/AC1.** A Measured leg with null geometry (shouldn't happen — 4.1 throws on Ok-without-geometry — but be defensive) draws dashed. This keeps the visual honest: solid lines exist **only** where a real road shape is known.
- **Air "great-circle" curve is OUT OF SCOPE (recommend defer).** UX-DR4 calls Air a "dashed great-circle"; Phase 1 (and this story) draw all non-geometry legs as **straight** dashed connectors. True great-circle interpolation for Air is cosmetic, unrelated to FR-6 (Measured road geometry), and would add curve-densification code. **Keep Air as the existing straight dashed connector**; if you think the curve is worth it, record it in `deferred-work.md` — do not expand this story.
- **Attribution lives with the provider** (`ITravelTimeProvider.Attribution`) so the data-licence obligation is declared where the data source is, and the UI just renders whatever the active provider declares (null ⇒ nothing). Do not sniff config in the component.
- **No `/table`, no LRM.** AR-7 names Leaflet Routing Machine + a custom `IRouter`; in practice the legs are plain `L.polyline`s and the server cache is already the single source of truth, so **LRM is not needed** — drawing the decoded geometry as an `L.polyline` fully satisfies FR-6/UX-DR4. Do **not** introduce LRM (it would be a redundant dependency that "never calls OSRM directly" anyway). If a reviewer flags the AR-7 deviation, the rationale is: the cache-backed polyline render achieves the same visible result with no extra plugin.

### Architecture compliance / guardrails

- **NFR8 (licensing):** OSM-derived routing ⇒ ODbL attribution visible on **both** surfaces when OSRM is active (AC4). **NFR5:** attribution copy via `UiStrings`. **NFR1/NFR3:** redraw stays incremental (layer replace, no re-init) and runs on the circuit via the existing `StateChanged` path.
- **Layering:** Component→ViewModel→Service→JS. The page calls `IMapService` (never JS directly for the new attribution call — mirror `DrawTripLegsAsync`, though note some existing one-off interops call JS directly; prefer the service method). No business logic in JS beyond decode+draw.
- **Build discipline:** `TreatWarningsAsErrors`, `Nullable`; no group-B analyzer violations; no `ConfigureAwait(false)`; System.Text.Json; no hardcoded UI string. Tag new code `TRIP-OSRM-02` (map render) / reference `TRIP-MAP-02`/`TRIP-OSRM-01`.
- **Dual-surface (UX-DR12, project-context):** desktop + `Mobile*Screen` share `LeafletMap.razor`, so one implementation covers both — but verify the mobile layout still shows the attribution (Leaflet's attribution control renders bottom-right on both; confirm it isn't clipped by the mobile map's ~46% height container).

### Testing standards

Three layers. Unit-test the projection (geometry flows for Measured, null otherwise) and the provider `Attribution`. Use `StubMapService` (extend it) to assert the DTO geometry + the attribution call + the upgrade re-push at the integration layer. The actual Leaflet decode→solid-road DOM and the attribution-control DOM are **JS-only** and not covered by the C# harness (`StubMapService` no-ops Leaflet — the same harness limitation as the 1-2 "Map-marker Stop-badge coverage" and 1-4 "popup non-regression" defers); verify by inspection and record the limitation. `InternalsVisibleTo` is set. **Run the Trip integration filter** after the projection/DI change (A3). Build/test commands:
- `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Trip integration: `dotnet test … --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`

### Project Structure Notes

- **Updated (production):** `TripProjections.cs` (`TripLeg.GeometryPolyline`), `TripViewModel.cs` (`MakeLeg` + maybe `RoutingAttributionHtml`), `LeafletMapService.cs` + `IMapService.cs` (`TripLegDto` geometry + `SetRoutingAttributionAsync`), `MapPage.razor` (project geometry, fix `LegsEqual`, one-time attribution call), `leafletInterop.js` (decoder + measured-geometry draw + `setRoutingAttribution`), `App.razor` (`?v=` bump), `ITravelTimeProvider.cs`/`MockTravelTimeProvider.cs`/`OsrmTravelTimeProvider.cs` (`Attribution`), `UiStrings.cs` (attribution copy).
- **Updated (tests):** `Integration/StubMapService.cs` (record geometry + attribution), new/extended projection + provider-attribution tests, an integration upgrade-repush test.
- **No EF migration** — reads existing `RouteSegment.GeometryPolyline`.

### References

- [Source: epics.md#Story-4.2] — ACs (FR-6, AR-7, NFR8, UX-DR4, UX-DR9, UX-DR12).
- [Source: epics.md — UX-DR4 (line solidity = geometric fidelity; Measured solid, all else dashed+muted; Air great-circle), UX-DR9 (Estimated→Measured upgrade lands via StateChanged), UX-DR12 (dual-surface + OSM/ODbL attribution), NFR8 (ODbL), AR-7 (D6 map rendering; LRM custom IRouter — see scope-guard deviation)].
- [Source: 4-1-osrm-measured-travel-time-provider.md] — geometry is `geometries=polyline` **precision 5** in `RouteSegment.GeometryPolyline`; OSRM is the OSM-based provider; `OsrmOptions.GeometryPrecision`. The decoder MUST match precision 5 (TRIP-OSRM-01).
- [Source: 1-3-render-ordered-stops-connecting-legs-and-the-stop-panel.md] — the Phase-1 `drawTripLegs` measured-branch scaffolding + `.trip-leg-measured` CSS this story activates (TRIP-MAP-02/03, TRIP-LEG-01/02).
- [Source: project-context.md] — Leaflet 1.9.4 self-hosted (ARCH-HIGH-07, no CDN); UiStrings for all UI text; dual render paths; token palette + dark mode; build/layering/testing rules.
- [Source: OSRM/Google encoded-polyline algorithm] — precision-5 decode to `[lat,lon]` vertices.

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (Claude Opus 4.8) — via bmad-story-automator manual cycle (no tmux, Windows).

### Debug Log References

- `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug` → **succeeded, 0 warnings / 0 errors** (TreatWarningsAsErrors clean; no group-B analyzer violations; no ConfigureAwait; no hardcoded UI string — attribution via UiStrings).
- Fast suite (`--filter "FullyQualifiedName!~Integration"`) → **Passed: 764, Failed: 0, Skipped: 0** (incl. new projection-geometry tests in `TripViewModelTravelTimeTests` and provider `Attribution` tests in `MockTravelTimeProviderTests` / `OsrmTravelTimeProviderTests`).
- Trip integration (`--filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`) → **Passed: 20, Failed: 0, Skipped: 0** (~43s; incl. the new `EstimatedLegs_PushNullGeometry_ThenMeasuredCacheFill_RePushesPolyline` upgrade re-push test). A3 integration host stays green.
- Post-review (attribution-seam unit tests added): focused `~Attribution` filter → **5 passed**; **full suite 913/914** — the single failure is the documented pre-existing `Union_ShowsAllUniquePois` integration flake (an Operations test unrelated to Trip/OSRM), which **passes in isolation** (re-verified). Effectively 914/914 green.

### Completion Notes List

- **Geometry threaded end-to-end** (Task 1/2/3): `RouteSegment.GeometryPolyline` → `TripLeg.GeometryPolyline` (`MakeLeg`) → `TripLegDto.GeometryPolyline` (`PushTripLegsAsync`) → `drawTripLegs(leg.geometryPolyline)`. JS decodes a precision-5 encoded polyline and draws solid/primary/full-weight only when geometry is present; otherwise the straight dashed+muted connector. **Solidity keys off geometry presence (`hasGeometry`), not `isMeasured` alone** (AC1), and a malformed string makes `decodePolyline` return null ⇒ straight-connector fallback, never throws (AC5).
- **AC3 dedup (LegsEqual):** `TripLeg` is a record, so `LegsEqual`'s `a[i].Equals(b[i])` now compares `GeometryPolyline` AND `IsMeasured` automatically. An Estimated→Measured upgrade with the same endpoints is therefore NOT equal to the prior dashed leg → the no-redraw guard does not swallow it → the leg redraws solid/road-shaped live via the existing progress→`RefreshLegsFromCache`→`Notify`→`PushTripLegsAsync` path. Proven end-to-end by the new integration test (fires `TravelTimeProgressService.Set` after a Measured cache fill; `StubMapService.LastTripLegGeometries` flips null→polyline).
- **Attribution without breaking the integration host (A3):** added `string? Attribution` to `ITravelTimeProvider` (Mock ⇒ null, OSRM ⇒ `UiStrings.TripRoutingAttributionOsm`). The VM surfaces it via `RoutingAttributionHtml`. To keep the parameterless `AddTripServices()` host able to construct the VM, the new `ITravelTimeProvider` ctor param is **OPTIONAL (default null)** AND the parameterless overload now registers the dependency-light haversine `MockTravelTimeProvider` (no Polly/hosted/HTTP deps; declares null attribution). The production `IConfiguration` overload re-registers the config-selected provider AFTER calling the parameterless one, and the last `ITravelTimeProvider` registration wins on resolve, so `"Osrm"` still swaps in cleanly in production. No existing VM-construction test call site changed (optional param).
- **Attribution push:** new `IMapService.SetRoutingAttributionAsync(string?)` → `leafletInterop.setRoutingAttribution(html)` adds the OSM/ODbL routing attribution to Leaflet's attribution control on top of the unchanged base tile attribution (null removes it). Called once per map instance from `MapPage.OnAfterRenderAsync` with `TripVm.RoutingAttributionHtml`; the JS helper is idempotent (removes the prior routing attribution first) so a viewport-flip re-call never double-prints. One call covers both desktop and mobile (shared `LeafletMap`).
- **No new bundled lib / no CDN (ARCH-HIGH-07):** the ~30-line `decodePolyline` (standard Google/OSRM precision-5 algorithm, factor 1e5) is inlined in `leafletInterop.js` with a comment tying it to Story 4.1's `geometries=polyline` precision 5 (TRIP-OSRM-01) — change the 1e5 factor to 1e6 in lockstep if 4.1 ever moves to precision 6.
- **CSS:** no change — `.trip-leg-measured { stroke: var(--primary…) }` + `dashArray:null` already yield solid+primary, dark-mode aware. `?v=` bumped 17→18 in `App.razor`.
- **Scope guards honoured:** Air stays a straight dashed connector (no great-circle); no Leaflet Routing Machine; no EF migration. JS decode→solid-road DOM + attribution-control DOM are JS-only and not C#-testable — recorded honestly in `deferred-work.md` (no faked JS assertion in C#).

### File List

**Production:**
- `LucidCartographer/Components/Shared/Trip/TripProjections.cs` — `TripLeg.GeometryPolyline` field.
- `LucidCartographer/Components/Shared/Trip/TripViewModel.cs` — `MakeLeg` populates `GeometryPolyline`; optional `ITravelTimeProvider` ctor param; `RoutingAttributionHtml` property.
- `LucidCartographer/Services/Trip/ITravelTimeProvider.cs` — `string? Attribution { get; }`.
- `LucidCartographer/Services/Trip/MockTravelTimeProvider.cs` — `Attribution => null`.
- `LucidCartographer/Services/Trip/OsrmTravelTimeProvider.cs` — `Attribution => UiStrings.TripRoutingAttributionOsm` (+ `using LucidCartographer.Services;`).
- `LucidCartographer/Services/UiStrings.cs` — `TripRoutingAttributionOsm` copy.
- `LucidCartographer/Services/IMapService.cs` — `SetRoutingAttributionAsync` declaration.
- `LucidCartographer/Services/LeafletMapService.cs` — `TripLegDto.GeometryPolyline`; `SetRoutingAttributionAsync` impl.
- `LucidCartographer/Components/Shared/LeafletMap.razor` — `SetRoutingAttributionAsync` forwarder.
- `LucidCartographer/Components/Pages/MapPage.razor` — `PushTripLegsAsync` projects geometry; `LegsEqual` comment (record equality now covers geometry/IsMeasured); one-time `SetRoutingAttributionAsync` after map init.
- `LucidCartographer/Configuration/TripServicesExtensions.cs` — register `MockTravelTimeProvider` in the parameterless overload.
- `LucidCartographer/wwwroot/js/leafletInterop.js` — `decodePolyline`; geometry-driven solid/dashed draw in `drawTripLegs`; `setRoutingAttribution`; `state.routingAttribution` reset in `initMap`.
- `LucidCartographer/Components/App.razor` — `leafletInterop.js?v=18` bump.

**Tests:**
- `LucidCartographer.Tests/Integration/StubMapService.cs` — record `LastTripLegGeometries` + `SetRoutingAttributionAsync` (`LastRoutingAttribution`, `RoutingAttributionWasSet`).
- `LucidCartographer.Tests/Integration/IntegrationTestBase.cs` — `GetAppService<T>()` root-singleton accessor.
- `LucidCartographer.Tests/Integration/TripViewIntegrationTests.cs` — `EstimatedLegs_PushNullGeometry_ThenMeasuredCacheFill_RePushesPolyline` (+ `WaitForLegGeometriesAsync`).
- `LucidCartographer.Tests/ViewModels/TripViewModelTravelTimeTests.cs` — projection geometry tests (Measured carries polyline; non-Measured/uncomputed null) + geometry param on `AddSegmentAsync`.
- `LucidCartographer.Tests/Services/MockTravelTimeProviderTests.cs` — `Attribution_IsNull`.
- `LucidCartographer.Tests/Services/OsrmTravelTimeProviderTests.cs` — `Attribution_IsOsmOdblRoutingString`.
- `LucidCartographer.Tests/ViewModels/TripViewModelRecomputeTests.cs` — stub providers implement `Attribution`.
- `LucidCartographer.Tests/Services/TravelTimeComputationBackgroundServiceTests.cs` — stub providers implement `Attribution`.

- `LucidCartographer.Tests/ViewModels/TripViewModelAttributionTests.cs` — **(review fix)** unit-guards the `TripViewModel.RoutingAttributionHtml` seam (null with no provider / under Mock; surfaces an OSM-based provider's attribution).

**Docs:**
- `_bmad-output/implementation-artifacts/deferred-work.md` — Story 4.2 harness-limitation + Air-curve + per-instance-attribution defers.

## Senior Developer Review (AI)

**Reviewer:** adversarial fresh-context review via bmad-story-automator manual cycle (claude-opus-4-8)
**Date:** 2026-06-14
**Outcome:** Approve-with-fixes (0 CRITICAL, 0 HIGH, 1 MED [fixed], LOW accepted)

Traced all eight probe areas against the real code (not the dev's claims) + built and ran the provider/projection suite. The two highest-risk paths held: **AC3 live upgrade** — `LegsEqual` uses `TripLeg.Equals` (positional record incl. `GeometryPolyline`+`IsMeasured`), so the Estimated→Measured upgrade is not swallowed (proven by the integration re-push test). **AC4 attribution on viewport flip** — `MapPage.OnAfterRenderAsync` re-wires off `!ReferenceEquals(_leafletMap, _wiredMap)` (a flip makes a new `LeafletMap`), so `SetRoutingAttributionAsync` re-fires and the idempotent JS re-adds it; attribution survives the desktop↔mobile flip. **DI ordering** — parameterless registers Mock, the config overload re-registers AFTER (last-wins → OSRM in prod); Mock's only dep resolves to default options; the optional VM ctor param broke no call site. `decodePolyline` is correct precision-5 with malformed→null→dashed fallback; solidity gates on geometry presence; build discipline + layering + UiStrings clean.

### Action Items

- [x] [AI-Review][MED] The attribution seam was unguarded — `TripViewModel.RoutingAttributionHtml` and `StubMapService.LastRoutingAttribution` had no asserting test, so a regression returning null under OSRM (or the page dropping the push) would stay green. Added `TripViewModelAttributionTests` (null with no provider / under the real Mock; surfaces an OSM-based provider's `UiStrings` attribution). Provider-level `Attribution` was already unit-tested for both Mock and OSRM.
- [x] [AI-Review][LOW] (accepted) Attribution string is plain text (no `<a>` link) — matches the spec's example copy; it's a compile-time `const` (no XSS). Per-instance (not per-circuit) attribution push and Air-stays-straight are recorded in `deferred-work.md`. Truncated-mid-coordinate polyline still falls back safely.

## Change Log

| Date | Change |
|------|--------|
| 2026-06-14 | Story 4.2 implemented: measured legs drawn road-shaped + OSM/ODbL routing attribution. `GeometryPolyline` threaded `RouteSegment`→`TripLeg`→`TripLegDto`→JS; inline precision-5 `decodePolyline` (no CDN, ARCH-HIGH-07) in `leafletInterop.js`; `drawTripLegs` gates solid/primary/full-weight on **geometry presence** (not `isMeasured` alone), malformed polyline falls back to the straight dashed connector. `LegsEqual` record-equality now covers `GeometryPolyline`+`IsMeasured` so the Estimated→Measured upgrade is not swallowed (AC3, proven by new integration re-push test). `ITravelTimeProvider.Attribution` (Mock→null, OSRM→`UiStrings.TripRoutingAttributionOsm`); VM `RoutingAttributionHtml`; new `IMapService.SetRoutingAttributionAsync`→`leafletInterop.setRoutingAttribution` (idempotent, both surfaces). A3 host kept green: optional VM ctor `ITravelTimeProvider` + Mock registered in the parameterless `AddTripServices()` overload (last-registration-wins keeps prod OSRM). Air stays straight-dashed (no great-circle); no LRM; no EF migration; `?v=`17→18. Build clean (0 warnings); 764 fast + 20 Trip integration green. JS render/attribution DOM noted as harness limitation in `deferred-work.md`. Status → review. |
| 2026-06-14 | Fresh-context adversarial review (0 CRITICAL / 0 HIGH / 1 MED / LOW). Verified AC3 live-upgrade (LegsEqual record-equality), AC4 attribution survives the desktop↔mobile viewport flip (re-wire keyed on map-instance identity), and the last-registration-wins DI ordering. **MED fixed:** added `TripViewModelAttributionTests` guarding the previously-untested `RoutingAttributionHtml` seam. LOW accepted. Full suite 913/914 (the 1 failure is the pre-existing `Union_ShowsAllUniquePois` flake, passes in isolation). Status → done. |
