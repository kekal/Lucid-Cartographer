---
baseline_commit: 59ca1ae951ba1a54dabd8f35e82f758f4bf83710
---

# Story 4.1: OSRM Measured travel-time provider

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a self-hoster who wants real road times,
I want an optional OSRM provider I can enable per deployment,
so that Drive/Walk/Cycle legs return measured durations and road geometry.

## Acceptance Criteria

_(FR-6 enabling, AR-3, NFR7, NFR9; epics.md#Story-4.1; + Epic-3 retro carry-ins A6/A9)_

1. **OSRM provider implemented + config-selected.** Given the existing `ITravelTimeProvider` contract, when `OsrmTravelTimeProvider` is implemented and selected via configuration (`TravelTime:Provider = "Osrm"`), then for a Drive/Walk/Cycle leg it queries the OSRM HTTP API and returns a `TravelLegResult` with **`Fidelity = Measured`**, a real road duration (seconds) and distance (meters), and encoded road **geometry** (`GeometryPolyline`). Walk → **foot**, Cycle → **bike**, Drive → **car** profiles. The **default remains Mock** — OSRM is opt-in, never the default (NFR9).
2. **Any/Air is never routed by OSRM.** Given a leg whose mode is `AnyAir`, when `OsrmTravelTimeProvider.GetLegAsync` is called, then it does **not** make an HTTP call and returns a straight-line **Placeholder** result (identical behaviour to `MockTravelTimeProvider` for Any/Air) — Air carries no road geometry (FR-8, AR-10).
3. **Out-of-coverage / no-route degrades, never errors (A6).** Given a Drive/Walk/Cycle leg whose endpoints OSRM cannot route (`code != "Ok"`, empty `routes`, unreachable host, timeout, or HTTP error), when the leg is computed, then the leg **degrades to the Estimated haversine fallback** through the *existing* degradation path in `TravelTimeComputationBackgroundService` (badged Estimated, `Source = EstimatedFallback`, never blank, no thrown error reaches the loop's caller). The provider signals "no usable route" by **throwing** so the existing `catch` branch (TRIP-DEGRADE-01) handles it — no second degradation path is introduced.
4. **No egress for self-hosted OSRM (NFR7).** Because OSRM is self-hosted, Stop coordinates stay within the deployment — there is **no out-call to a third party** and therefore **no egress-consent guard** is required for OSRM. (The firm-consent egress guard applies only if a future *out-calling* provider is ever added; this story adds none.) A short note to that effect lives in the provider/config docs.
5. **Optional, opt-in docker-compose sidecar.** Given an operator who opts into OSRM, when they enable it, then OSRM runs as a **docker-compose sidecar** using `ghcr.io/project-osrm/osrm-backend` (**version-pinned**, not `:latest`) with a region-scoped OSM extract, gated behind an **opt-in compose profile** so the default `docker compose up` (Mock deployment) starts none of it. The compose additions and a short "How to enable OSRM" doc (extract prep + the per-profile containers + the matching `TravelTime` config) are committed.
6. **Directional, per-leg (A9).** Each directional leg `A→B` is a separate OSRM `/route` query; the provider never mirrors `A→B` onto `B→A`. (OSRM Drive routes can be genuinely asymmetric — one-way streets — and the directional `RouteSegment` cache [TRIP-CACHE-01] already keys on direction.)
7. **Estimated→Measured upgrade still works (A6).** Given legs currently cached as Estimated / EstimatedFallback after OSRM becomes available, when the user invokes the existing "Recompute travel times" action, then those recompute-eligible rows are deleted and refilled by the background service — now yielding **Measured** rows from OSRM (the existing upgrade mechanism from Story 2.4, no new code needed beyond confirming `EstimatedFallback` rows are eligible). Manual and Measured rows are never overwritten/invalidated.

## Tasks / Subtasks

- [x] **Task 1 — `OsrmOptions` + config wiring** (AC: #1, #5)
  - [x] Add `Services/Trip/OsrmOptions.cs` (sealed, mirrors `TravelTimeOptions` conventions): per-profile base URLs `DriveBaseUrl`/`WalkBaseUrl`/`CycleBaseUrl` (string?, each independently optional — a mode with no URL configured is treated as "no coverage" → degrades to Estimated, AC3), a `RequestTimeoutSeconds` (default ~10), and a `GeometryPrecision`/encoding note. **Per-profile URLs** because an OSRM backend process serves exactly the one profile its extract was built with (AR-3 "per-profile container") — a single base URL cannot serve car+foot+bike.
  - [x] Add a `Provider` selector to the `TravelTime` config section (or a small `TravelTimeProviderOptions`): `TravelTime:Provider` ∈ {`"Mock"` (default), `"Osrm"`}. Default/missing ⇒ Mock.
  - [x] In `Configuration/TripServicesExtensions.cs` `AddTripServices(IConfiguration)`: **replace the hard-coded** `AddSingleton<ITravelTimeProvider, MockTravelTimeProvider>()` with a config switch — `"Osrm"` ⇒ register `OsrmTravelTimeProvider` (+ a **named `IHttpClientFactory` client** `"osrm"`, timeout from options, a `LucidCartographer/<ver>` User-Agent) and bind `OsrmOptions` from `TravelTime:Osrm`; anything else ⇒ `MockTravelTimeProvider` as today. **Leave the parameterless `AddTripServices()` overload untouched** (no provider, no hosted service — the integration-host seam; A3 standing gate).
  - [x] Add the new keys (commented, like the existing `TravelTime` block) to `appsettings.json` with `Provider: "Mock"` and an `Osrm` sub-block whose profile URLs are empty/sample-commented so the shipping default is unchanged.
- [x] **Task 2 — `OsrmTravelTimeProvider`** (AC: #1, #2, #3, #6)
  - [x] New `Services/Trip/OsrmTravelTimeProvider.cs` (sealed, primary-constructor DI: `IHttpClientFactory`, `IOptions<OsrmOptions>`, `IOptions<TravelTimeOptions>` for the Any/Air + degrade estimate, `ILogger`). `Source => "OSRM"` (add the constant to `TravelTimeSource`).
  - [x] `GetLegAsync(from, to, travelMode, ct)`:
    - `AnyAir` ⇒ return `EstimatedTravelTime.Compute(...) with { Fidelity = Placeholder }` (mirror `MockTravelTimeProvider`); **no HTTP** (AC2).
    - Drive/Walk/Cycle ⇒ resolve the profile (`Drive→car`, `Walk→foot`, `Cycle→bike`) and its base URL from `OsrmOptions`. If that profile has **no URL configured**, **throw** (→ degradation, AC3). Otherwise build `GET {base}/route/v1/{profile}/{fromLon},{fromLat};{toLon},{toLat}?overview=full&geometries=polyline&alternatives=false&steps=false` (note: **lon,lat order**, OSRM convention).
    - Parse with **System.Text.Json**: require `code == "Ok"` and a non-empty `routes[0]`; read `duration` (seconds, double → `(int)Math.Round`), `distance` (meters, double), `geometry` (encoded polyline string). Return `new TravelLegResult(seconds, meters, Fidelity.Measured, geometry)`.
    - `code != "Ok"` (e.g. `"NoRoute"`/`"NoSegment"`), empty routes, non-success HTTP, `HttpRequestException`, or timeout (`TaskCanceledException` when `!ct.IsCancellationRequested`) ⇒ **throw** a clear exception (a small `OsrmRouteUnavailableException` or `InvalidOperationException` with the OSRM `code`/status in the message) so the existing background-service `catch` degrades the leg to Estimated (AC3). **Re-throw `OperationCanceledException` when `ct` is cancelled** (do not swallow real cancellation).
  - [x] Tag new decisions with `TRIP-OSRM-01` (provider), and reference `[TRIP-CACHE-01]` where directionality matters (AC6).
- [x] **Task 3 — docker-compose sidecar + enablement doc** (AC: #5)
  - [x] Add the OSRM services to `LucidCartographer/docker-compose.yml` under a **`profiles: ["osrm"]`** gate (so they only start with `docker compose --profile osrm up`): one container per profile the operator wants (e.g. `osrm-car`, `osrm-foot`, `osrm-bike`) on `ghcr.io/project-osrm/osrm-backend:<pinned-tag>`, each running `osrm-routed` against a region extract mounted read-only, exposing distinct host ports. Wire the matching `TravelTime__Provider=Osrm` and `TravelTime__Osrm__{Drive,Walk,Cycle}BaseUrl` env (commented sample) on the `cartographer` service — **do not** turn OSRM on by default.
  - [x] Add `docs/osrm.md` (or a section in the existing deployment doc — search `docs/` first): how to fetch a region `.osm.pbf`, run `osrm-extract`/`osrm-partition`/`osrm-customize` (MLD) per profile, and the exact `docker compose --profile osrm up` + config to flip it on. Keep it short and factual (UX-DR11 voice rules apply to docs too).
- [x] **Task 4 — A6 confirmation: EstimatedFallback rows are recompute-eligible** (AC: #7)
  - [x] Verify (and test) that `RouteSegmentInvalidationService.InvalidateRecomputableForCollectionAsync` deletes `Source == EstimatedFallback` rows — they carry `Fidelity == Estimated`, so the existing `Fidelity != Manual && Fidelity != Measured` predicate already includes them. If a gap exists, fix it; otherwise add a regression test asserting an `EstimatedFallback` row is recomputed (and a `Measured` row is **not**). No new recompute UI — reuse Story 2.4's action.
  - [x] **A6 (scope invalidation to the collection's pairs):** the 2.4 review noted `InvalidateRecomputableForCollectionAsync` matches any row whose *both* endpoints are in the collection (a cross-collection shared row could be redundantly invalidated). Either tighten it to the collection's **ordered consecutive pairs** (+ closing leg) under the active mode, or — if that materially complicates the query — **explicitly defer** it in Completion Notes + `deferred-work.md` with rationale (low impact: shared rows just recompute once more). Do not silently leave it unaddressed.
- [x] **Task 5 — Tests** (AC: all)
  - [x] **Provider unit tests** (`LucidCartographer.Tests/Services/OsrmTravelTimeProviderTests.cs`): drive the provider over a **stubbed `HttpMessageHandler`** (no real OSRM). Cover: (a) a successful `/route` → `Measured` with correct seconds/meters/geometry and the right profile token + **lon,lat** order in the URL; (b) `code: "NoRoute"` ⇒ throws; (c) empty `routes` ⇒ throws; (d) HTTP 500 / `HttpRequestException` / timeout ⇒ throws; (e) **AnyAir ⇒ no HTTP call** (assert the handler was never invoked) and Placeholder fidelity; (f) a profile with no configured URL ⇒ throws; (g) `Source == "OSRM"`. Assert real cancellation re-throws `OperationCanceledException`.
  - [x] **Degradation integration with the background loop** (extend `TravelTimeComputationBackgroundServiceTests`): when the active provider throws (the OSRM no-route case), the loop writes an Estimated row with `Source = EstimatedFallback` (never blank, never errors) — confirms AC3 wiring end-to-end through the real degradation branch.
  - [x] **Config-selection test:** `AddTripServices(config)` with `TravelTime:Provider = "Osrm"` resolves `ITravelTimeProvider` to `OsrmTravelTimeProvider`; default/`"Mock"`/missing resolves to `MockTravelTimeProvider`.
  - [x] **A6 recompute test** (Task 4): `EstimatedFallback` row is recompute-eligible; `Measured`/`Manual` rows are preserved.
  - [x] **Trip integration filter** — run after the DI change in `TripServicesExtensions` (A3 standing gate: this is a provider-registration change in the overload the production host calls; confirm the host still boots and the parameterless test-host overload is unchanged). VM constructor is **not** expected to change.
- [x] **Task 6 — Build & full-suite verification**
  - [x] `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug` → clean (0 warnings; no group-B analyzer violations; no `ConfigureAwait(false)`; no new `MA0026`).
  - [x] Fast unit/component pass, then full suite incl. Trip integration. Record counts in Debug Log.

## Dev Notes

### The one decision that matters most — geometry encoding & `/route` vs `/table`

- **Encoding:** AR-3 literally says `geometries=geojson`, but the persisted field is **`RouteSegment.GeometryPolyline` (a single string)** and Story 4.2 requires geometry "stored/encoded **one consistent way** project-wide". Use **`geometries=polyline`** (OSRM's encoded-polyline, precision 5) and store that string verbatim in `GeometryPolyline`. Rationale: matches the field name, far more compact than a geojson coordinate array, and decodes natively in Leaflet/Leaflet-Routing-Machine (Story 4.2). This is a deliberate, documented deviation from AR-3's literal text — same road geometry, better storage shape. **Tag it `TRIP-OSRM-01` and call it out in Completion Notes** so the reviewer and Story 4.2 inherit the same encoding. (If the dev prefers `polyline6`/precision 6, that's fine **only if** Story 4.2's decoder is set to the same precision — pick one and state it.)
- **`/route` per leg is the contract; `/table` is OUT OF SCOPE here.** The `ITravelTimeProvider` seam is **per-leg** (`GetLegAsync(from, to, mode)`), and `/route` for a single pair returns duration **and** distance **and** geometry in one call — fully satisfying FR-6 and AC1's "Measured + geometry". `/table` returns durations/distances for many pairs but **no geometry**, and wiring it would require extending the provider seam and the cache-population path. The N×N matrix is already built by `DistanceMatrixService` from the **shared cache** (D2a — the cache is the single source of truth; the matrix never calls a provider), so a `/table` prefetch is purely a cache-warming optimization, not a functional requirement. **Implement `/route` per leg; do not implement `/table`.** If you think a batch `/table` warm-up is worth it, record it as a deferred optimization in `deferred-work.md` — do not expand this story.

### How the provider seam works here (read first)

- **Contract** (`Services/Trip/ITravelTimeProvider.cs`): `string Source { get; }` + `Task<TravelLegResult> GetLegAsync(TravelEndpoint from, TravelEndpoint to, string travelMode, CancellationToken ct)`. `TravelEndpoint(int PoiId, double Latitude, double Longitude)`. `TravelLegResult(int DurationSeconds, double DistanceMeters, string Fidelity, string? GeometryPolyline)`. **Mirror `MockTravelTimeProvider.cs` exactly** for shape/conventions — it is the reference implementation.
- **The provider is called only from the background service.** `TravelTimeComputationBackgroundService` calls `provider.GetLegAsync(...)` inside the **`"travel-time"` Polly pipeline** (retry + timeout, already configured in `Configuration/ResilienceExtensions.cs`). On **any** exception it already falls back: `EstimatedTravelTime.Compute(...)` + `Source = TravelTimeSource.EstimatedFallback` + a `LogWarning` (TRIP-DEGRADE-01). **This is exactly why the OSRM provider should throw on no-route** — you get the degradation, observability, and "never blank" guarantee for free (AC3). Do **not** add a second fallback inside the provider.
- **Cache write** is done by the background service's `UpsertAsync` under `SqliteWriteLock`, and it **never overwrites `Manual` or `Measured` rows** — so a Measured OSRM row, once written, sticks until explicitly invalidated. You do not write the cache from the provider.
- **DI overloads** (`Configuration/TripServicesExtensions.cs`): parameterless `AddTripServices()` = VM-facing services only (no provider, no hosted service) — **this is the integration-host seam; do not add a provider here**. `AddTripServices(IConfiguration)` calls the parameterless one then adds the provider + `AddHostedService<TravelTimeComputationBackgroundService>()` + `Configure<TravelTimeOptions>`. Your config switch goes in **the configuration overload only**.

### Key source files

- `LucidCartographer/Services/Trip/MockTravelTimeProvider.cs` — **the pattern to mirror** (Any/Air → Placeholder; everything else via `EstimatedTravelTime`).
- `LucidCartographer/Services/Trip/EstimatedTravelTime.cs` — `Compute(from, to, mode, options)` → haversine `TravelLegResult` (Estimated, null geometry). Reuse for Any/Air and (indirectly, via the loop's catch) for degradation.
- `LucidCartographer/Services/Trip/TravelTimeOptions.cs` — options conventions + `SpeedFor(mode)`; `Services/Trip/OsrmOptions.cs` is **new** and mirrors it.
- `LucidCartographer/Services/Trip/TravelTimeSource.cs` — add `public const string Osrm = "OSRM";` next to `Mock`/`Manual`/`EstimatedFallback`.
- `LucidCartographer/Services/Trip/TravelTimeComputationBackgroundService.cs` — the only caller; the `try`/`catch` degradation branch (TRIP-DEGRADE-01) and `UpsertAsync` (Manual/Measured protection). **Read it** to confirm AC3/AC7 wiring; you should not need to change it (the catch is already `catch (Exception)`).
- `LucidCartographer/Services/Trip/RouteSegmentInvalidationService.cs` (+ interface) — `InvalidateRecomputableForCollectionAsync` (Fidelity != Manual && != Measured). Task 4 verifies/tightens this.
- `LucidCartographer/Configuration/TripServicesExtensions.cs` — the two overloads; the hard-coded provider registration you replace with a switch.
- `LucidCartographer/Configuration/PoiServicesExtensions.cs` — **the `IHttpClientFactory` named-client pattern to mirror** (timeout, User-Agent, `ConfigurePrimaryHttpMessageHandler`). Use a named client `"osrm"`.
- `LucidCartographer/Configuration/ResilienceExtensions.cs` — the `"travel-time"` Polly pipeline already wraps provider calls; no change needed.
- `LucidCartographer/Data/Entities/RouteSegment.cs`, `Fidelity.cs`, `TravelMode.cs` — directional cache key; string-backed Fidelity/TravelMode with EF check constraints; `Measured` is the Epic-4 value.
- `LucidCartographer/appsettings.json` — the commented `TravelTime` block to extend; `LucidCartographer/docker-compose.yml` — where the profile-gated OSRM services go.

### Architecture compliance / guardrails

- **AR-3 / AR-11 / AR-12:** `OsrmTravelTimeProvider` lives in the `Services/Trip/` vertical slice, interface-first (it implements the existing `ITravelTimeProvider`). Canonical units at the edge: **seconds**, **meters**; OSRM already returns seconds/meters (just round duration to int). Directional cache ([TRIP-CACHE-01]) — never assume `A→B == B→A` (A9). Tag new code `TRIP-OSRM-01`.
- **NFR2 (graceful degradation):** the haversine fallback must keep every leg populated when OSRM can't serve it — achieved by throwing into the existing degradation branch (AC3). **NFR6 (observability):** the existing `LogWarning` distinguishes degraded vs Measured; keep it.
- **NFR7/NFR9:** self-hosted OSRM ⇒ no egress, no per-request cost; opt-in only. **NFR8 (ODbL/OSM attribution) is Story 4.2**, not here — but the compose/docs may note that enabling OSRM brings the attribution obligation (which 4.2 renders).
- **Build discipline:** `TreatWarningsAsErrors=true`, `Nullable=enable`; no group-B analyzer violations (`MA0002/0015/0046/0047/0074`, `VSTHRD200`); **no `ConfigureAwait(false)`**; no new `MA0026` (TODO). System.Text.Json (not Newtonsoft). Use `IHttpClientFactory`/named client — never `new HttpClient()`.
- **Concurrency / atomicity (A7, tracked):** out of scope for this story (it adds no new ordering writer). Do not touch the OrderIndex write path.

### Testing standards

Three layers (project-context.md). The provider is pure I/O glue → unit-test it with a **stubbed `HttpMessageHandler`** over `IHttpClientFactory` (construct an `HttpClient` on the stub handler, wrap in a tiny test `IHttpClientFactory`); **no real OSRM, no network**. `InternalsVisibleTo("LucidCartographer.Tests")` is set. Run the **Trip integration filter** after the `TripServicesExtensions` DI change (the recurring integration-host regression point — A3). Build/test commands:
- `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Trip integration: `dotnet test … --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`
- Full suite ~5 min, ~886 tests as of Epic 3.

### Project Structure Notes

- **New (production):** `Services/Trip/OsrmTravelTimeProvider.cs`, `Services/Trip/OsrmOptions.cs` (and a small `OsrmRouteUnavailableException` if used).
- **New (tests):** `LucidCartographer.Tests/Services/OsrmTravelTimeProviderTests.cs`.
- **Updated:** `Configuration/TripServicesExtensions.cs` (provider config switch + `"osrm"` named client), `Services/Trip/TravelTimeSource.cs` (`Osrm` const), `appsettings.json` (`Provider` + `Osrm` block), `docker-compose.yml` (profile-gated OSRM services), `docs/` (enablement note). Possibly `TravelTimeComputationBackgroundServiceTests` / `RouteSegmentInvalidationTests` for AC3/AC7 coverage.
- **No EF migration** — `RouteSegment` already has `GeometryPolyline`, `Fidelity`, `Source` (added in Story 1.1); this story only writes new *values* (`Measured`, `"OSRM"`) into existing columns.

### References

- [Source: epics.md#Story-4.1] — ACs (FR-6 enabling, AR-3, NFR7, NFR9).
- [Source: epics.md — AR-3 (D2/D2a OSRM), AR-7 (D6 LRM, geometry consumed in 4.2), AR-2 (provider contract), AR-10 (Any/Air assumed speed)].
- [Source: 2-3-graceful-degradation-to-straight-line-estimates.md] — TRIP-DEGRADE-01, the existing degradation branch this story routes no-route into (A6).
- [Source: 2-4-cache-invalidation-recompute-estimated-measured-upgrade.md] — the Recompute action + Estimated→Measured upgrade reused for AC7; `InvalidateRecomputableForCollectionAsync` scoping note (A6).
- [Source: epic-3-retro-2026-06-14.md] — A6 (Epic-4 carry-ins: provider-recovery upgrade, real no-route signal, scope invalidation), A9 (treat the cache as directional/asymmetric — OSRM can be genuinely asymmetric), A3 (Trip integration after DI change).
- [Source: project-context.md] — build/layering/testing/units rules; `IHttpClientFactory` + System.Text.Json + Polly conventions.
- [Source: OSRM HTTP API] — `/route/v1/{profile}/{coordinates}` returns `{ code, routes:[{ duration, distance, geometry }] }`; coordinates are `lon,lat`; `code` is `"Ok"`/`"NoRoute"`/`"NoSegment"`; `geometries=polyline` ⇒ encoded-polyline string; profiles car/foot/bike per backend extract.

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (Claude Opus 4.8) — via bmad-story-automator manual cycle (no tmux, Windows).

### Debug Log References

- `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug` → **Build succeeded, 0 Warning(s), 0 Error(s)** (TreatWarningsAsErrors=true; no group-B analyzer violations; no `ConfigureAwait(false)`; no new `MA0026`).
- `dotnet build LucidCartographer.Tests/LucidCartographer.Tests.csproj -c Debug` → **0 Warning(s), 0 Error(s)**.
- New/affected focused filter (`Osrm | TripProviderConfigSelection | RouteSegmentInvalidation | TravelTimeComputationBackgroundService`) → **32 passed, 0 failed**.
- Fast suite (`FullyQualifiedName!~Integration`) → **758 passed, 0 failed, 0 skipped** (~5 s).
- Trip integration filter (`FullyQualifiedName~Integration&FullyQualifiedName~Trip`, REQUIRED after the TripServicesExtensions DI change — A3 gate) → **19 passed, 0 failed, 0 skipped** (~49 s). Production host still boots; parameterless overload untouched.
- Post-review fix (geometry-absent guard) re-verified: focused `Osrm|ProviderConfig` filter → **18 passed**; **full suite 905/905 passed, 0 failed** (5m40s, incl. Trip integration).

### Completion Notes List

- **Geometry encoding (TRIP-OSRM-01):** requested `geometries=polyline` (OSRM encoded-polyline, **precision 5**) and stored verbatim in `RouteSegment.GeometryPolyline`. This is the deliberate, documented deviation from AR-3's literal `geometries=geojson` (matches the field name, far more compact, decodes natively in Leaflet). **Story 4.2's decoder MUST use precision 5.** `OsrmOptions.GeometryPrecision` (default 5) carries the knob; precision 6 (`polyline6`) is supported in `BuildRouteUri` if 4.2 ever switches — pick one and keep both ends in sync.
- **`/route` per leg only; no `/table`.** Each directional A→B leg is its own `/route` query (lon,lat order, OSRM convention); A→B is never mirrored onto B→A (A9 / TRIP-CACHE-01). `/table` is recorded as a deferred cache-warming optimization in `deferred-work.md`.
- **Degrade-by-throwing (AC3):** the provider throws `OsrmRouteUnavailableException` on `code != "Ok"`, empty routes, non-success HTTP, `HttpRequestException`, timeout, and unconfigured-profile — routing the leg into the existing `TravelTimeComputationBackgroundService` catch (TRIP-DEGRADE-01) which writes `Estimated` / `Source = EstimatedFallback`. No second fallback inside the provider. Real cancellation (`ct` cancelled) re-throws `OperationCanceledException`; a `TaskCanceledException` while `ct` is NOT cancelled (HttpClient timeout) is treated as no-route. The background service was **not** modified — its `catch (Exception)` already handled it.
- **Any/Air (AC2):** no HTTP at all — returns a straight-line `Placeholder` result, mirroring `MockTravelTimeProvider`. Unit test asserts the stub handler is never invoked.
- **Config switch (AC1, NFR9):** the switch lives in the `AddTripServices(IConfiguration)` overload ONLY. Default / `"Mock"` / missing / unrecognized ⇒ `MockTravelTimeProvider` (case-insensitive on `"Osrm"`). The parameterless overload is untouched (integration-host seam, A3). `"Osrm"` registers `OsrmTravelTimeProvider` + a named `IHttpClientFactory` client `"osrm"` (timeout from `OsrmOptions`, `LucidCartographer/1.0` User-Agent) + binds `OsrmOptions` from `TravelTime:Osrm`.
- **Per-profile URLs (AR-3):** Drive→car / Walk→foot / Cycle→bike each carry an independent optional base URL; a mode with no URL has no coverage and degrades. A single `osrm-routed` process serves exactly one profile.
- **NFR7 (no egress):** documented in `OsrmOptions`/`OsrmTravelTimeProvider`/`docs/osrm.md` — OSRM is self-hosted, coordinates never leave the deployment, so no egress-consent guard is required.
- **AC7 (Estimated→Measured upgrade):** confirmed `EstimatedFallback` rows are recompute-eligible — they carry `Fidelity.Estimated`, so the existing `Fidelity != Manual && != Measured` predicate in `InvalidateRecomputableForCollectionAsync` already deletes them; `Measured`/`Manual` rows survive. No code change needed; added an explicit regression test. No new recompute UI (reuses Story 2.4's action).
- **A6 deferral (scope invalidation to the collection's ordered pairs):** explicitly **deferred** with rationale in `deferred-work.md` (low impact — a cross-collection shared row recomputes once more; tightening would duplicate the background service's ordered-pair logic inside the query). Not silently left unaddressed.
- **docker-compose:** added `osrm-car`/`osrm-foot`/`osrm-bike` sidecars gated behind `profiles: ["osrm"]` on `ghcr.io/project-osrm/osrm-backend:v6.0.0` (version-pinned, not `:latest`), each running `osrm-routed --algorithm mld` against a read-only region extract on distinct host ports (5000/5001/5002). Commented `TravelTime__Provider=Osrm` + per-profile base-URL env on the `cartographer` service — OSRM is OFF by default.
- **No EF migration** — `RouteSegment` already has `GeometryPolyline`/`Fidelity`/`Source`; this story only writes new values (`Measured`, `"OSRM"`).

### File List

**New (production):**
- `LucidCartographer/Services/Trip/OsrmTravelTimeProvider.cs`
- `LucidCartographer/Services/Trip/OsrmOptions.cs`
- `LucidCartographer/Services/Trip/OsrmRouteUnavailableException.cs`

**New (tests):**
- `LucidCartographer.Tests/Services/OsrmTravelTimeProviderTests.cs`
- `LucidCartographer.Tests/Services/TripProviderConfigSelectionTests.cs`

**New (docs):**
- `docs/osrm.md`

**Modified (production):**
- `LucidCartographer/Services/Trip/TravelTimeSource.cs` (added `Osrm` const)
- `LucidCartographer/Configuration/TripServicesExtensions.cs` (provider config switch + `"osrm"` named client; configuration overload only)
- `LucidCartographer/appsettings.json` (`TravelTime:Provider` + `TravelTime:Osrm` block, commented, shipping default = Mock)
- `LucidCartographer/docker-compose.yml` (profile-gated OSRM sidecars + commented provider env)

**Modified (tests / docs):**
- `LucidCartographer.Tests/Services/TravelTimeComputationBackgroundServiceTests.cs` (OSRM-no-route → EstimatedFallback degradation test)
- `LucidCartographer.Tests/Services/RouteSegmentInvalidationTests.cs` (A6 EstimatedFallback-eligible / Measured-preserved regression test)
- `LucidCartographer.Tests/Services/OsrmTravelTimeProviderTests.cs` (review fix: `Ok`-but-no-geometry → throws regression test)
- `_bmad-output/implementation-artifacts/deferred-work.md` (A6 scope-invalidation + `/table` deferrals)

## Senior Developer Review (AI)

**Reviewer:** adversarial fresh-context review via bmad-story-automator manual cycle (claude-opus-4-8)
**Date:** 2026-06-14
**Outcome:** Approve-with-fixes (0 CRITICAL, 0 HIGH, 2 MED [1 fixed, 1 accepted], LOW accepted/deferred)

Verified all 7 ACs against the diff and the upstream `TravelTimeComputationBackgroundService` catch: the degrade-by-throwing wiring is airtight (every no-route path throws `OsrmRouteUnavailableException`; the existing `catch (Exception)` writes `Estimated`/`EstimatedFallback`; genuine cancellation re-throws in both the provider filter and the loop). Any/Air makes no HTTP call (handler asserted un-invoked). Config selection defaults to Mock case-insensitively; the parameterless integration-host overload is untouched. lon,lat order + invariant-culture formatting + duration rounding + profile tokens all correct and asserted. docker-compose is version-pinned and profile-gated (off by default); `docs/osrm.md` matches; build discipline clean (no ConfigureAwait/Newtonsoft/raw HttpClient/MA0026/hardcoded UI text). File List == `git status`.

### Action Items

- [x] [AI-Review][MED] A `code:"Ok"` OSRM response with null/empty `geometry` was persisted as a `Measured` row with no polyline — violates AC1 and, because `Upsert` never overwrites `Measured`, would permanently deny Story 4.2 its road line with no recompute path. Fixed: `MapResponse` throws `OsrmRouteUnavailableException` on blank geometry (degrades to Estimated); regression test added.
- [x] [AI-Review][MED] (accepted) `OsrmOptions.RequestTimeoutSeconds` is not read by the provider — the operational timeout is set on the named HttpClient at registration via the same `TravelTime:Osrm:RequestTimeoutSeconds` key with the same default (10), so there is no possible divergence. Kept as the documented config knob.
- [x] [AI-Review][LOW] (accepted) `Math.Max(1, timeoutSeconds)` defensive clamp; `polyline6` branch untested (4.2 is pinned to precision 5); `/table` and A6 scope-invalidation deferred with rationale in `deferred-work.md`.

## Change Log

| Date | Change |
|------|--------|
| 2026-06-14 | Story 4.1 implemented: `OsrmTravelTimeProvider` (per-leg `/route`, lon,lat, `geometries=polyline` precision 5 → `Measured`; throws-to-degrade on no-route/HTTP-error/timeout/unconfigured-profile; Any/Air → Placeholder, no HTTP; re-throws real cancellation), `OsrmOptions` (per-profile Drive→car/Walk→foot/Cycle→bike URLs), `OsrmRouteUnavailableException`, `TravelTimeSource.Osrm`. Config switch in `AddTripServices(IConfiguration)` only (default Mock, NFR9) + named `"osrm"` HttpClient. appsettings `TravelTime:Provider`/`Osrm` block (Mock default). Profile-gated OSRM docker-compose sidecars (pinned `v6.0.0`, off by default) + `docs/osrm.md`. AC7 confirmed (EstimatedFallback recompute-eligible, regression test); A6 scope-invalidation + `/table` deferred with rationale. No EF migration. Build clean (0 warnings); 758 fast + 19 Trip integration green. Status → review. |
| 2026-06-14 | Fresh-context adversarial review (0 CRITICAL / 0 HIGH / 2 MED / ~4 LOW). **MED-1 fixed:** a `code:"Ok"` route with null/empty `geometry` was silently persisted as `Measured` with no polyline (violates AC1, and `Measured` rows are never re-invalidated → would permanently starve Story 4.2). `MapResponse` now treats blank geometry as "no usable route" and throws → the leg degrades to Estimated; added a regression test. MED-2 (the bound `OsrmOptions.RequestTimeoutSeconds` is mirrored by the registration's direct read of the same key — no possible divergence) accepted as documented. LOWs accepted/deferred. Full suite **905/905 green**. Status → done. |
