---
baseline_commit: da4b8882dc712d36259447726126bcea11e1c153
---

# Story 2.2: ValhallaTravelTimeProvider (measured, all ground modes)

Status: done

## Story

As a deployment operator,
I want a Valhalla-backed provider that returns measured duration, distance, and road geometry for every ground mode,
So that trip legs can show real road-network travel times instead of estimates.

## Acceptance Criteria

1. **Given** the seam scaffolding from Story 2.1 (`ITravelTimeProvider.ProducesMeasuredFidelity`, `TravelTimeSource.Valhalla`, `UiStrings.TripRoutingAttributionValhalla` all already exist) and a reachable Valhalla engine, **When** I add `ValhallaTravelTimeProvider`, `ValhallaOptions`, and `ValhallaRouteUnavailableException` in `LucidCartographer/Services/Trip/` (sealed, primary-constructor DI, mirroring the OSRM provider shape), **Then** they compile into the contract with XML doc comments in the established file style (FR-5, AD-3).
2. **And** the provider issues a single `/route` POST per leg, mapping Drive→`auto`, Walk→`pedestrian`, Cycle→`bicycle` against one configured base URL (FR-5, FR-6, AD-3).
3. **And** it parses `trip.summary.time` to `int` seconds (rounded) and `trip.summary.length` km→×1000 meters at the provider edge only, returning Fidelity **Measured** with the encoded route geometry (FR-5, FR-7, NFR-11).
4. **And** geometry is treated as **precision 6** (polyline6): `ValhallaOptions.GeometryPrecision` defaults to `6`, and the `LeafletMap` JS decoder (`leafletInterop.js#decodePolyline`) is updated/verified to decode at precision 6 (factor `1e-6`, not `1e-5`) so the map renders the Valhalla polyline correctly (FR-7, AD-3).
5. **And** request coordinates are sent as `{lat, lon}` JSON formatted with `CultureInfo.InvariantCulture` (AD-3 provider-swap trap — comma-decimal corruption guard).
6. **And** `ProducesMeasuredFidelity` returns `true`, `Source` returns `TravelTimeSource.Valhalla`, and `Attribution` returns `UiStrings.TripRoutingAttributionValhalla` (FR-10, NFR8).
7. **And** Air/AnyAir legs return **Placeholder** *without* issuing an HTTP request (FR-9).
8. **And** a timeout, HTTP error, no-route response, or missing/blank geometry on a `Measured` leg throws `ValhallaRouteUnavailableException`; a genuine caller-token cancellation re-throws `OperationCanceledException` instead (AD-3 — a null-geometry Measured row must never persist).
9. **And** `ValhallaOptions` binds from `TravelTime:Valhalla` with defaults `BaseUrl=http://valhalla:8002`, `RequestTimeoutSeconds=10`, `GeometryPrecision=6`, using a named `"valhalla"` `IHttpClientFactory` client.
10. **And** unit tests (mirroring `OsrmTravelTimeProviderTests`) cover the costing map, km→m and seconds conversion, precision-6 geometry handling, the Air-skips-HTTP path, and each failure→`ValhallaRouteUnavailableException` path.
11. **And** the solution compiles clean under `TreatWarningsAsErrors` with no group-B analyzer violations (MA0002/MA0015/MA0046/MA0047/MA0074/VSTHRD200) (NFR-12).

## Architecture & Code Context

This is the **measured provider** for Epic 2. It is **purely additive**: a new provider class plus its options and exception, mirroring the existing OSRM trio shape. It is **not yet wired into DI** — Story 2.4 does the `AddTripServices(IConfiguration)` `=="Valhalla"` branch and adds the `appsettings.json` section. The recompute trigger that consumes `ProducesMeasuredFidelity==true` is Story 2.3. So after this story the class exists and is fully unit-tested but is not reachable at runtime until 2.4. **Do not delete or touch any OSRM artifact** — OSRM removal is Epic 3.

The single non-obvious cross-file touch is the JavaScript polyline decoder (AC 4): Valhalla emits **polyline6**, the existing decoder hardcodes precision-5 (`1e-5`). This must be reconciled so the measured polyline renders correctly. See the dedicated subsection below.

### `LucidCartographer/Services/Trip/ValhallaTravelTimeProvider.cs` (NEW)

Mirror `OsrmTravelTimeProvider.cs` exactly in shape — same sealed class, same primary-constructor DI, same try/catch degrade-via-exception discipline, same `using` block lifetime on the `HttpResponseMessage`, same `OperationCanceledException` re-throw-vs-wrap split.

- **Signature / DI:** `public sealed class ValhallaTravelTimeProvider(IHttpClientFactory httpClientFactory, IOptions<ValhallaOptions> valhallaOptions, IOptions<TravelTimeOptions> travelTimeOptions, ILogger<ValhallaTravelTimeProvider> logger) : ITravelTimeProvider`.
- **`public const string HttpClientName = "valhalla";`** — the named `IHttpClientFactory` client (OSRM uses `"osrm"`).
- **Seam members (Story 2.1 already provides the constants/strings):**
  - `public string Source => TravelTimeSource.Valhalla;`
  - `public string? Attribution => UiStrings.TripRoutingAttributionValhalla;`
  - `public bool ProducesMeasuredFidelity => true;`
- **`GetLegAsync(TravelEndpoint from, TravelEndpoint to, string travelMode, CancellationToken ct)`:**
  1. **Air/AnyAir short-circuit (AC 7) — copy the OSRM pattern verbatim:** if `string.Equals(travelMode, TravelMode.AnyAir, StringComparison.Ordinal)`, compute `EstimatedTravelTime.Compute(from, to, travelMode, travelTimeOptions.Value)` and return `estimate with { Fidelity = Fidelity.Placeholder }`. **No HTTP call.** (OSRM does exactly this at lines 48–53.)
  2. **Costing map (AC 2):** resolve the Valhalla costing string from the ground mode — Drive→`"auto"`, Walk→`"pedestrian"`, Cycle→`"bicycle"`. Put this in a `ValhallaOptions.CostingFor(travelMode)` static (mirrors `OsrmOptions.ProfileFor`). A mode with no costing (i.e. anything not Drive/Walk/Cycle that reached here) ⇒ throw `ValhallaRouteUnavailableException` (parallels OSRM's "no configured profile" throw). Note Valhalla, unlike OSRM, has **one base URL** for all modes (one engine, dynamic costing) — there is no per-mode URL.
  3. **Build the request:** Valhalla's `/route` is a **POST** with a JSON body (OSRM was a GET with a URL). Body shape:
     ```json
     {
       "locations": [ { "lat": <from.Latitude>, "lon": <from.Longitude> },
                      { "lat": <to.Latitude>,   "lon": <to.Longitude> } ],
       "costing": "<auto|pedestrian|bicycle>",
       "directions_options": { "units": "kilometers" }
     }
     ```
     - Coordinates are `{lat, lon}` (Valhalla is lat/lon — **opposite** of OSRM's lon,lat). **AC 5: format every coordinate with `CultureInfo.InvariantCulture`** so a comma-decimal locale never corrupts the body. The cleanest path is to serialize a DTO whose `double` values are written by `System.Text.Json` (which is invariant by default for numbers) — but if you build any string manually, you MUST pass `CultureInfo.InvariantCulture`. Prefer a typed request record serialized with `JsonSerializer` to avoid the trap entirely.
     - `directions_options.units = "kilometers"` makes `trip.summary.length` a value in **km** (you then ×1000 → meters). Setting units explicitly removes ambiguity.
     - To request **polyline6** geometry, Valhalla uses `"shape_format"` — but the default Valhalla `/route` response already encodes `trip.legs[].shape` as **precision-6** encoded polyline, which is the value to store. Treat `GeometryPrecision` as the contract the decoder must match (default 6), not as a thing you toggle per request.
  4. **Send:** `var client = httpClientFactory.CreateClient(HttpClientName); response = await client.PostAsync(requestUri, content, ct);` inside the same try/catch as OSRM:
     - `catch (OperationCanceledException) when (ct.IsCancellationRequested)` → `throw;` (re-throw genuine cancellation).
     - `catch (OperationCanceledException ex)` → wrap in `ValhallaRouteUnavailableException` (HttpClient timeout, caller token NOT cancelled).
     - `catch (HttpRequestException ex)` → wrap in `ValhallaRouteUnavailableException`.
  5. **Inside `using (response)`:** non-success status ⇒ throw `ValhallaRouteUnavailableException` with the status code. Deserialize the stream with `JsonException` → throw `ValhallaRouteUnavailableException` ("unparseable response"). `OperationCanceledException when ct.IsCancellationRequested` → re-throw.
  6. **Map the response (AC 3, AC 8):** Valhalla success JSON is `{ "trip": { "status": 0, "summary": { "time": <sec double>, "length": <km double> }, "legs": [ { "shape": "<polyline6>" } ] } }`.
     - `trip.status != 0` (or a present `trip.status_message` indicating no route, or a top-level error object) ⇒ throw `ValhallaRouteUnavailableException`.
     - `seconds = (int)Math.Round(trip.summary.time)`.
     - `meters = trip.summary.length * 1000.0` (km→m at the edge — **NFR-11, no mid-layer conversion**).
     - `geometry = trip.legs[0].shape`. **If `string.IsNullOrWhiteSpace(geometry)` ⇒ throw `ValhallaRouteUnavailableException("... no geometry ...")`** — a geometry-less Measured row would be pinned by the Upsert Measured-guard and never repaired (this is the exact rule OSRM enforces at lines 149–154).
     - Return `new TravelLegResult(seconds, meters, Fidelity.Measured, geometry)`.
     - Log a debug line mirroring OSRM's "Measured leg via Valhalla — {Seconds}s / {Meters}m".
  - **Private nested response DTOs** (mirror OSRM's `OsrmRouteResponse`/`OsrmRoute` private sealed classes): a `ValhallaRouteResponse { Trip }`, `ValhallaTrip { Status, Summary, Legs }`, `ValhallaSummary { Time, Length }`, `ValhallaLeg { Shape }`, with `[JsonPropertyName(...)]` attributes and a shared `JsonSerializerOptions { PropertyNameCaseInsensitive = true }`.

### `LucidCartographer/Services/Trip/ValhallaOptions.cs` (NEW)

Mirror `OsrmOptions.cs` but **simpler** — one base URL, not three (one engine, dynamic costing):

- `public string BaseUrl { get; set; } = "http://valhalla:8002";`
- `public int RequestTimeoutSeconds { get; set; } = 10;`
- `public int GeometryPrecision { get; set; } = 6;` — **XML doc MUST state the decoder must match this precision** (the OSRM options doc says the same about its decoder; for Valhalla the factor is `1e-6`).
- `public static string? CostingFor(string travelMode)` ⇒ Drive→`"auto"`, Walk→`"pedestrian"`, Cycle→`"bicycle"`, `_ => null` (mirrors `OsrmOptions.ProfileFor`; reference `Data.Entities.TravelMode.Drive/Walk/Cycle`).
- Bound from the `TravelTime:Valhalla` section (the actual `appsettings.json` entry + DI binding is **Story 2.4**, not here — but the option class and its defaults land now).

### `LucidCartographer/Services/Trip/ValhallaRouteUnavailableException.cs` (NEW)

Copy `OsrmRouteUnavailableException.cs` exactly, renamed: a `sealed` exception with `(string message)` and `(string message, Exception innerException)` constructors and an XML `<summary>` describing it as the signal that Valhalla could not produce a usable Measured route (unreachable, timeout, no-route, or missing geometry), thrown to trigger degradation to Estimated; distinct from `OperationCanceledException`, which is re-thrown.

### `LucidCartographer/wwwroot/js/leafletInterop.js` — `decodePolyline` (UPDATE — the precision-6 reconciliation)

This is the **only existing file this story must change**, and it is load-bearing for AC 4.

- Today `decodePolyline` (around lines 210–245) hardcodes precision 5: it pushes `[lat * 1e-5, lng * 1e-5]`, and a comment explicitly says *"If 4.1 ever moves to precision 6 (polyline6), change the 1e5 factor here to 1e6."* Valhalla **is** that move.
- Valhalla `trip.legs[].shape` is **polyline6**. Decoding precision-6 data with the `1e-5` factor renders the route at ~1/10th scale (wildly wrong location). So the factor MUST become `1e-6`.
- **Caveat — OSRM still exists this epic.** OSRM (still present until Epic 3) emits precision-5 (`OsrmOptions.GeometryPrecision` default 5) via the same `decodePolyline`. A blunt global flip to `1e-6` would break OSRM-sourced geometry while OSRM is still a selectable provider. Options, in order of preference:
  1. **Confirm OSRM's effective precision in this deployment.** If no deployment currently runs OSRM measured (default is Mock; OSRM is opt-in and being retired next epic), a straight flip to `1e-6` is acceptable — but update the comment to say the decoder now targets Valhalla polyline6, and note OSRM (precision-5) will mis-decode until Epic 3 removes it. Call this out explicitly in the Dev Agent Record.
  2. **Pass precision through** from the C# side (the `TripLegDto` could carry a precision/`isMeasured` hint, or `drawTripLegs` could read a configured precision) and select `1e-5`/`1e-6` per leg. This is more robust but is a larger change touching `MapPage.razor`'s `TripLegDto` projection — only do this if the costing/wiring naturally supports it; otherwise prefer option 1 and document the trade-off.
- **Whichever you choose, AC 4 requires the decoder to correctly render Valhalla precision-6 geometry**, and the change must be reflected/verified (a JS-level check or a documented manual verification, since there is no JS test harness). Update the stale comment so the next reader knows precision-6 is now the Valhalla contract.

### Verified existing contracts (read before coding)

- **`ITravelTimeProvider`** (`Services/Trip/ITravelTimeProvider.cs`) — already carries `Source`, `Attribution`, `ProducesMeasuredFidelity`, `GetLegAsync`. `TravelEndpoint` is `readonly record struct TravelEndpoint(int PoiId, double Latitude, double Longitude)`.
- **`TravelLegResult`** (`Services/Trip/TravelLegResult.cs`) — `readonly record struct (int DurationSeconds, double DistanceMeters, string Fidelity, string? GeometryPolyline)`. Units are seconds + meters, fixed at the edge.
- **`Fidelity`** constants — `Fidelity.Measured`, `Fidelity.Estimated`, `Fidelity.Placeholder` (string constants in `Data.Entities`).
- **`TravelMode`** constants — `TravelMode.Drive/Walk/Cycle/AnyAir` (`Data.Entities`).
- **`TravelTimeSource.Valhalla == "Valhalla"`** and **`UiStrings.TripRoutingAttributionValhalla`** — both shipped by Story 2.1 (done). Do not re-add.
- **`EstimatedTravelTime.Compute(from, to, travelMode, travelTimeOptions.Value)`** — the haversine edge reused for the Air/AnyAir placeholder; identical call to the OSRM provider's.

## Constraints (NFRs)

- **NFR-11 — Canonical units.** Duration → `int` seconds, distance → meters, converted **only** at the provider edge (`length` km × 1000). No conversion anywhere downstream.
- **NFR7 — Privacy (HARD).** Valhalla is self-hosted; the provider must contact **only** the one configured `BaseUrl` host and issue no other outbound call. The automated no-egress assertion is Story 2.6, but write the provider so it is trivially true (single base URL, no fallback host, no telemetry).
- **NFR8 — Attribution.** `Attribution => UiStrings.TripRoutingAttributionValhalla` — sourced from `UiStrings`, never a hardcoded literal in the provider (mirror `OsrmTravelTimeProvider.Attribution`).
- **NFR-12 — Build discipline.** Clean under `TreatWarningsAsErrors` + analyzers; public members need XML doc comments (group-B doc analyzer).
- **AD-3 — Valhalla provider contract.** This story *is* AD-3: costing map auto/pedestrian/bicycle, single `/route` POST, edge conversions (seconds + km→m), polyline6 with matching decoder, named `"valhalla"` client, `{lat,lon}` invariant-culture JSON, missing-geometry throw, defaults BaseUrl/Timeout/Precision.
- **Additive / no regression.** OSRM provider, options, exception, source constant, attribution string, `"osrm"` client — all stay untouched. No DI change (that's 2.4). No `appsettings.json` Valhalla section yet (that's 2.4). The only existing file edited is the JS decoder.

## Testing

Add `ValhallaTravelTimeProviderTests` (mirror `LucidCartographer.Tests/Services/OsrmTravelTimeProviderTests.cs` structure — the same `StubHandler : HttpMessageHandler` + `StubHttpClientFactory` + `Json(...)` helpers, recording `CallCount`/`LastRequestUri`). Use `namespace LucidCartographer.Tests` (flat — the Story 2.1 note warns that a folder-shaped `LucidCartographer.Tests.Services` namespace shadows `LucidCartographer.Services.*` and breaks the build).

Cover (AC 10):
- **`Source_IsValhalla`** — `provider.Source == "Valhalla"` and `== TravelTimeSource.Valhalla`.
- **`Attribution_IsValhallaOdblString`** — `== UiStrings.TripRoutingAttributionValhalla`, non-empty.
- **`ProducesMeasuredFidelity_IsTrue`**.
- **Costing map** — `[Theory]` over `(Drive,"auto") (Walk,"pedestrian") (Cycle,"bicycle")`: a success body returns `Fidelity.Measured`, and the POST body (capture via the stub reading `request.Content`) contains the expected `"costing":"<value>"`.
- **Conversion** — a success body with `time:1234.6` ⇒ `DurationSeconds == 1235` (rounded); `length` in km (e.g. `56.789`) ⇒ `DistanceMeters == 56789.0` (×1000 at edge).
- **Geometry verbatim** — the encoded `shape` is returned in `GeometryPolyline` unchanged (precision-6 string stored as-is; the decode happens in JS).
- **Invariant culture** — the serialized coordinates use `.` decimal separators regardless of thread culture (optionally run under a comma-decimal culture to lock the AD-3 trap).
- **Air/AnyAir** — `CallCount == 0`, `Fidelity.Placeholder`, `GeometryPolyline` null, distance > 0.
- **Failure → exception** — separate facts for: `trip.status != 0` / no-route; empty/missing `legs`; blank `shape` (message contains "no geometry"); HTTP 500; `HttpRequestException`; `TaskCanceledException` (timeout, caller token not cancelled). Each ⇒ `ValhallaRouteUnavailableException`.
- **Real cancellation** — a handler that cancels the caller's token and throws `OperationCanceledException(token)` ⇒ re-throws `OperationCanceledException` (NOT wrapped).

Run the fast suite and the Trip integration filter; this story adds an unwired class so both must stay green.

## Build/Test commands

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast tests: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Valhalla tests only: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Valhalla"`
- Trip integration: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`

## Dev Notes

- **Provider shape is a near-mechanical mirror of OSRM** — the value of reading `OsrmTravelTimeProvider.cs` first cannot be overstated. The differences are exactly four: (1) POST+JSON-body instead of GET+URL, (2) `{lat,lon}` instead of `lon,lat`, (3) one base URL instead of per-mode, (4) response shape `trip.summary.{time,length}` + `trip.legs[].shape` instead of `routes[].{duration,distance,geometry}` with `length` in km not meters.
- **The polyline6 decoder change (AC 4) is the easy-to-miss item.** OSRM's own decoder comment literally points at this exact migration. Do not ship the provider without reconciling the JS factor, and document which option (straight flip vs per-leg precision) you took and why.
- **No DI wiring in this story.** If you find yourself editing `TripServicesExtensions`/`AddTripServices` or `appsettings.json`, stop — that is Story 2.4. This story ends at a unit-tested, un-registered class plus the JS decoder fix.
- **`ProducesMeasuredFidelity => true` is the gate Story 2.3 reads** to mark estimated rows upgrade-eligible. Returning `true` here is what makes the later recompute trigger fire for Valhalla but never for Mock.
- Story 2.1 (seam scaffolding) is **done** — the three seam touchpoints (interface bool, `Valhalla` source constant, Valhalla attribution string) already exist; this story consumes them, it does not create them.

### References

- [Source: _bmad-output/planning-artifacts/epics.md#Story 2.2] — acceptance criteria
- [Source: _bmad-output/planning-artifacts/epics.md#Additional Requirements] — AD-3 (Valhalla provider contract), AD-2 (capability flag consumed downstream)
- [Source: _bmad-output/planning-artifacts/architecture.md] — AD-3 provider-swap traps (lat/lon order, invariant culture, km→m, polyline6 decoder match)
- [Source: LucidCartographer/Services/Trip/OsrmTravelTimeProvider.cs] — the provider shape to mirror (Air short-circuit, try/catch degrade, missing-geometry throw, response mapping)
- [Source: LucidCartographer/Services/Trip/OsrmOptions.cs] — the options shape to mirror (defaults, `ProfileFor`→`CostingFor`)
- [Source: LucidCartographer/Services/Trip/OsrmRouteUnavailableException.cs] — the exception to copy/rename
- [Source: LucidCartographer/Services/Trip/ITravelTimeProvider.cs] — interface + `TravelEndpoint`
- [Source: LucidCartographer/Services/Trip/TravelLegResult.cs] — result record (seconds/meters/fidelity/geometry)
- [Source: LucidCartographer.Tests/Services/OsrmTravelTimeProviderTests.cs] — the test file to mirror (stub handler, theory over modes, failure facts)
- [Source: LucidCartographer/wwwroot/js/leafletInterop.js#decodePolyline] — precision-5→6 reconciliation (the `1e-5`→`1e-6` factor + stale comment)
- [Source: _bmad-output/implementation-artifacts/stories/story-2-1-provider-capability-seam-valhalla-source-and-attribution-scaffolding.md] — already-shipped seam scaffolding this story consumes

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (BMAD dev-story workflow)

### Debug Log References

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug` → Build succeeded, 0 Warning(s), 0 Error(s) (clean under TreatWarningsAsErrors, no group-B analyzer violations).
- Valhalla tests: `--filter "FullyQualifiedName~Valhalla"` → 21 passed / 0 failed.
- Fast suite: `--filter "FullyQualifiedName!~Integration"` → 1008 passed / 0 failed.
- Trip integration: `--filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"` → 20 passed / 0 failed.

### Completion Notes List

- Added the Valhalla trio (`ValhallaTravelTimeProvider`, `ValhallaOptions`, `ValhallaRouteUnavailableException`) under `Services/Trip/`, mirroring the OSRM shape: sealed, primary-constructor DI, same try/catch degrade-via-exception discipline, same `using (response)` lifetime, same `OperationCanceledException` re-throw-vs-wrap split. (AC1)
- Single `/route` **POST** per leg with a JSON body. Costing map lives in `ValhallaOptions.CostingFor`: Drive→`auto`, Walk→`pedestrian`, Cycle→`bicycle`; an unsupported mode throws `ValhallaRouteUnavailableException` without an HTTP call. One base URL for all modes (one engine, dynamic costing). (AC2)
- Edge conversions only (NFR-11): `seconds = (int)Math.Round(trip.summary.time)`, `meters = trip.summary.length * 1000.0`. Returns `Fidelity.Measured` with `trip.legs[0].shape` verbatim. (AC3)
- **Precision-6 decoder reconciliation (AC4) — chose Option 1 (straight flip).** `ValhallaOptions.GeometryPrecision` defaults to 6; `leafletInterop.js#decodePolyline` factor changed `1e-5` → `1e-6` and the stale comment rewritten. Rationale: deployment default provider is **Mock** (verified in `appsettings.json`: `TravelTime:Provider = "Mock"`), OSRM is opt-in only and being removed in Epic 3, so no precision-5 source is active. **Trade-off documented in the JS comment and here:** OSRM-sourced geometry (precision-5) will mis-decode at ~1/10th scale with the new factor until Epic 3 removes OSRM; the robust per-leg precision-hint approach (Option 2) is deferred to Epic 3. No JS test harness exists, so verification is by code review of the factor + matching `GeometryPrecision` default.
- Coordinates sent as `{lat, lon}` (opposite OSRM's lon,lat) via a typed request record serialized by `System.Text.Json`, which writes `double` values invariant-culture by default — sidesteps the AD-3 comma-decimal trap. A test runs under `de-DE` to lock this. (AC5)
- Seam members consume Story 2.1 scaffolding unchanged: `Source => TravelTimeSource.Valhalla`, `Attribution => UiStrings.TripRoutingAttributionValhalla`, `ProducesMeasuredFidelity => true`. (AC6)
- Air/AnyAir short-circuits to a haversine `Placeholder` with **no HTTP call** (copied OSRM pattern verbatim). (AC7)
- Failure→exception paths: timeout (caller token not cancelled), HTTP error, non-success status, unparseable JSON, `trip.status != 0`, empty/missing legs, and blank shape all throw `ValhallaRouteUnavailableException` (blank-shape message contains "no geometry"); genuine caller-token cancellation re-throws `OperationCanceledException`. (AC8)
- `ValhallaOptions` defaults: `BaseUrl=http://valhalla:8002`, `RequestTimeoutSeconds=10`, `GeometryPrecision=6`; named `"valhalla"` `IHttpClientFactory` client via `HttpClientName`. **No DI wiring / no appsettings section** added — that is Story 2.4. OSRM artifacts untouched. (AC9)
- Unit tests added (`ValhallaTravelTimeProviderTests`, flat `LucidCartographer.Tests` namespace) mirroring `OsrmTravelTimeProviderTests`: costing-map theory, km→m + seconds conversion, verbatim precision-6 geometry, Air-skips-HTTP, invariant-culture body under comma-decimal culture, and each failure→exception path plus real-cancellation re-throw. 21 facts, all green. (AC10)
- Solution compiles clean under `TreatWarningsAsErrors` with no group-B analyzer violations. (AC11)

### File List

**New:**
- `LucidCartographer/Services/Trip/ValhallaTravelTimeProvider.cs`
- `LucidCartographer/Services/Trip/ValhallaOptions.cs`
- `LucidCartographer/Services/Trip/ValhallaRouteUnavailableException.cs`
- `LucidCartographer.Tests/Services/ValhallaTravelTimeProviderTests.cs`

**Modified:**
- `LucidCartographer/wwwroot/js/leafletInterop.js` (decodePolyline: `1e-5` → `1e-6`, comment rewritten for the Valhalla polyline6 contract)

### Change Log

| Date       | Change                                                                                     |
|------------|--------------------------------------------------------------------------------------------|
| 2026-06-24 | Implemented Story 2.2: Valhalla measured provider trio + precision-6 decoder flip + tests. |
| 2026-06-24 | Senior Developer Review (AI) — Approve. All 11 ACs verified; build clean, 1012 fast + 20 Trip integration + 25 Valhalla tests green. No CRITICAL/HIGH/MEDIUM; 2 LOW notes (deferred). |

## Senior Developer Review (AI)

**Reviewer:** satec\yurik (autonomous story-automator review)
**Date:** 2026-06-24
**Outcome:** ✅ **Approve** — Status → done

### Scope

Story 2.2 surface only (the Valhalla measured-provider feature). The working tree also
contains intermingled, separately-reviewed Epic 1 (detour factors) and Story 2.1 (provider
seam) changes; those were explicitly excluded from this review and not flagged.

Reviewed files:
- `LucidCartographer/Services/Trip/ValhallaTravelTimeProvider.cs` (new)
- `LucidCartographer/Services/Trip/ValhallaOptions.cs` (new)
- `LucidCartographer/Services/Trip/ValhallaRouteUnavailableException.cs` (new)
- `LucidCartographer/wwwroot/js/leafletInterop.js` (decodePolyline precision flip)
- `LucidCartographer.Tests/Services/ValhallaTravelTimeProviderTests.cs` (new)

### Acceptance Criteria — all IMPLEMENTED

| AC | Verdict | Evidence |
|----|---------|----------|
| 1 — trio created, sealed, primary-ctor DI, XML docs, mirrors OSRM | ✅ | `ValhallaTravelTimeProvider.cs:19-23`; options + exception mirror OSRM shape |
| 2 — single `/route` POST, costing map, one base URL | ✅ | `CostingFor` Drive→auto/Walk→pedestrian/Cycle→bicycle (`ValhallaOptions.cs:37-43`); single `PostAsync` to `{baseUrl}/route` (`:65-73`) |
| 3 — seconds + km→m edge conversion, Measured + geometry | ✅ | `(int)Math.Round(Time)`, `Length*1000.0` (`:172-173`); returns `Fidelity.Measured` with shape |
| 4 — precision-6 + decoder `1e-6` | ✅ | `GeometryPrecision=6`; `leafletInterop.js:242` flips both lat/lng to `1e-6`; stale comment rewritten (`:210-217`) documenting the OSRM precision-5 trade-off (Option 1, straight flip) |
| 5 — `{lat,lon}` invariant-culture JSON | ✅ | typed record serialized by System.Text.Json (invariant for doubles); locked by `de-DE` test (`Tests:132-157`) |
| 6 — Source/Attribution/ProducesMeasuredFidelity | ✅ | `TravelTimeSource.Valhalla`, `UiStrings.TripRoutingAttributionValhalla`, `true` (`:33-42`) |
| 7 — Air skips HTTP, returns Placeholder | ✅ | AnyAir short-circuit before any client call (`:51-55`); test asserts `CallCount==0` |
| 8 — all failure paths → exception; real cancel re-throws | ✅ | timeout/HTTP/non-success/unparseable/status≠0/missing-summary/empty-legs/blank-shape all throw `ValhallaRouteUnavailableException`; genuine token cancel re-throws (`:75-78`, `:105-108`) |
| 9 — ValhallaOptions defaults + named "valhalla" client | ✅ | BaseUrl/Timeout=10/Precision=6; `HttpClientName="valhalla"`; no DI wiring (correctly deferred to Story 2.4) |
| 10 — unit tests mirror OSRM | ✅ | 21 facts: costing theory, conversions, verbatim geometry, Air-skips-HTTP, invariant culture, every failure path, real cancellation |
| 11 — clean under TreatWarningsAsErrors | ✅ | build 0 warnings / 0 errors |

### Findings

No CRITICAL, HIGH, or MEDIUM issues. Two LOW/informational notes, neither warranting a fix in this story:

- **LOW (informational) —** `ValhallaOptions.RequestTimeoutSeconds` is defined but the provider does not set `client.Timeout`; the named-client timeout binding belongs to the Story 2.4 DI registration (same pattern as OSRM). The timeout *handling* path is tested. Correct deferral.
- **LOW (cosmetic) —** the debug log at `ValhallaTravelTimeProvider.cs:175-177` passes the literal `"present"` for `{HasGeometry}` since geometry is already guaranteed non-blank by the preceding throw. Harmless; no behavioral impact.

### NFR / contract checks

- NFR-11 (canonical units, edge-only conversion): ✅ km→m and seconds rounding only at provider edge.
- NFR7 (privacy / single host): ✅ contacts only the one configured `BaseUrl`, no fallback host, no telemetry; `GetLeg_TargetsConfiguredBaseUrl` locks single-host + trailing-slash trim.
- NFR8 (attribution from UiStrings, not literal): ✅.
- Additive / no regression: ✅ no OSRM artifact touched; no DI or appsettings change (deferred to 2.4); only existing file edited is the JS decoder, as specified.

### Test results

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug` → 0 Warning(s), 0 Error(s).
- Valhalla filter: 25 passed / 0 failed.
- Fast suite (`!~Integration`): 1012 passed / 0 failed.
- Trip integration (`~Integration&~Trip`): 20 passed / 0 failed.

No auto-fixes applied — none required.
