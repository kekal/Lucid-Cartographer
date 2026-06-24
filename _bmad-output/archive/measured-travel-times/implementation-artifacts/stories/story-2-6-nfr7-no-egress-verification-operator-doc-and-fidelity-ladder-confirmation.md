---
baseline_commit: da4b8882dc712d36259447726126bcea11e1c153
---

# Story 2.6: NFR7 no-egress verification, operator doc, and fidelity-ladder confirmation

Status: done

## Story

As a deployment operator,
I want proof and documentation that stop coordinates never leave my deployment at any rung, plus a coherent fidelity ladder,
So that I can trust the hard privacy guarantee and read each leg's honesty badge at a glance.

## Acceptance Criteria

1. **Given** the hard privacy constraint NFR7 (stop coordinates must never leave the deployment at any fidelity rung) and the existing two-badge fidelity model, **When** I add an automated no-egress test, **Then** it asserts (a) the active **default** provider — smart-haversine, i.e. `MockTravelTimeProvider` — issues **no** outbound HTTP for a ground leg (it computes **in-process**, touching no `HttpClient`/`IHttpClientFactory` at all), and (b) the `ValhallaTravelTimeProvider` contacts **only** the single configured internal base-URL host (the request URI's host/port equals the configured `BaseUrl`, and **no** other host is ever contacted) for a ground leg, and issues **no** HTTP at all for an Air/AnyAir leg (NFR7, AD-5).
2. **And** the no-egress test is built on the **existing** stubbed `HttpMessageHandler` pattern from `ValhallaTravelTimeProviderTests` (a `StubHandler` capturing `CallCount` + `LastRequestUri`, injected via a `StubHttpClientFactory`) — **no real network**, no live Valhalla. For the Mock half, prove "no `HttpClient`" structurally: `MockTravelTimeProvider`'s constructor takes only `IOptions<TravelTimeOptions>` (no `IHttpClientFactory`/`HttpClient` dependency), so it is physically incapable of an out-call; the test computes a Drive/Walk/Cycle leg and asserts a real `Estimated` result with **no** handler/factory in play. For the Valhalla half, reuse the captured-`LastRequestUri` host assertion (mirroring the existing `GetLeg_TargetsConfiguredBaseUrl_WithTrailingSlashTrimmed` test) and the captured-`CallCount == 0` assertion for Air (mirroring `GetLeg_AnyAir_MakesNoHttpCall_AndIsPlaceholder`).
3. **And** a new operator document is added at **`docs/valhalla.md`** (replacing the **role** of `docs/osrm.md` — but **NOT** deleting `docs/osrm.md`; that deletion is Epic 3 / Story 3.3 / FR-14). It mirrors the structure and tone of `docs/osrm.md` and describes: (a) **turnkey setup** (`docker compose --profile valhalla up`, set `tile_urls`, set `TravelTime__Provider=Valhalla` — the exact three-step enable from Story 2.5); (b) **region selection** (the single `tile_urls` knob → a Geofabrik `.osm.pbf`); (c) the **expected one-time tile-build cost** (time / disk / RAM for the operator's region) — measured during implementation where feasible (OQ-3 / NFR-9), and where **not** measurable in this environment, the published gis-ops/docker-valhalla guidance documented and explicitly marked `[ASSUMPTION]` / operator-verify; (d) the **privacy guarantee** (NFR7) restated; and (e) a **documented operator check** that no stop-coordinate egress occurs during normal routing (FR-13, NFR7, AD-5).
4. **And** the trip view presents the coherent fidelity ladder using the **existing** badges only — **Estimated** (smart-haversine default & provider-failure fallback), **Measured** (Valhalla), **Manual** (user-entered), and **Placeholder / "—"** for un-routable Air — with **NO new badge type** introduced (FR-17). This AC is a **confirmation**, not a build: the four-rung ladder is already rendered by the existing `FidelityBadge.razor` (labels for Measured/Estimated/Manual; Placeholder/null render the em-dash, no pill). No production rendering code changes; the confirmation is satisfied by the existing `FidelityBadge` tests staying green plus the no-downgrade test below — do **not** add a third visible tier.
5. **And** no **Manual** or **Measured** cache row is downgraded or deleted across the estimate→measured progression (NFR-10 counter-metric). This is delivered by the **already-built** upsert guard `[TRIP-MANUAL-01]` in `TravelTimeComputationBackgroundService.UpsertAsync` (early-return when `existing.Fidelity is Fidelity.Manual or Fidelity.Measured`) and the `IsUpgradeEligible` read-side gate (only `Estimated`/`Placeholder` from `Mock`/`EstimatedFallback` are upgrade-eligible). The story adds a focused **NFR-10 no-downgrade test** asserting that, given a pre-existing Manual row and a pre-existing Measured row, a subsequent measured-provider pass (the estimate→measured progression) leaves **both** rows byte-for-byte intact (duration/distance/fidelity/source unchanged) — **no** new guard code (FR-17, NFR-10).
6. **And** any automatable validation passes: the new no-egress test and the NFR-10 no-downgrade test are green; the existing `FidelityBadge`/Valhalla/background-service tests stay green; the build is clean under `TreatWarningsAsErrors` with no group-B analyzer violations (MA0002/MA0015/MA0046/MA0047/MA0074/VSTHRD200); the Trip integration filter passes (NFR-12, NFR-13). The tile-build cost figures in `docs/valhalla.md` are an operator/manual measurement (OQ-3), **not** a CI test.

## Architecture & Code Context

This is **AD-5** — the NFR7 verification + operator documentation step that closes Epic 2, plus the **FR-17** fidelity-ladder confirmation. It is the last story of Epic 2 (Stories 2.1–2.5 done). The work is almost entirely **new test code + one new markdown doc**; **no production C# change is required** to satisfy any AC. Both behavioral guarantees the tests verify — in-process estimation (Mock) / single-host containment (Valhalla), and the Manual/Measured no-downgrade — are **already implemented** by Stories 1.2 / 2.2 / 2.3. This story **proves** them automatically and **documents** the privacy/turnkey story for the operator.

The change is: (1) **one new test file** (or a small set of tests) holding the NFR7 no-egress assertions and the NFR-10 no-downgrade assertion, and (2) **one new doc** `docs/valhalla.md`. Do **not** delete `docs/osrm.md` (Epic 3 / FR-14 owns that). Do **not** add a new badge type or touch any rendering code (FR-17 is reuse-only).

### What is already built (read, do NOT re-implement)

- **Mock computes in-process, no HttpClient** — `LucidCartographer/Services/Trip/MockTravelTimeProvider.cs`. Its **primary constructor takes only `IOptions<TravelTimeOptions>`** — there is no `IHttpClientFactory`/`HttpClient` dependency anywhere. `GetLegAsync` calls `EstimatedTravelTime.Compute(...)` and returns synchronously via `Task.FromResult`. This is the structural proof for AC-1a: the default provider is **physically incapable** of an out-call. `ProducesMeasuredFidelity => false`, `Attribution => null`.
- **Valhalla contacts exactly one configured host** — `LucidCartographer/Services/Trip/ValhallaTravelTimeProvider.cs`. `GetLegAsync` builds `requestUri = $"{baseUrl.TrimEnd('/')}/route"` from `valhallaOptions.Value.BaseUrl` and POSTs **once** via the named `"valhalla"` `IHttpClientFactory` client. Air/AnyAir returns a haversine estimate **before** any HTTP (`if (string.Equals(travelMode, TravelMode.AnyAir, …)) { … return estimate with { Fidelity = Placeholder }; }`). This is the behavior AC-1b asserts.
- **The stubbed-handler test pattern** — `LucidCartographer.Tests/Services/ValhallaTravelTimeProviderTests.cs`. It already contains exactly the seam the no-egress test reuses:
  - `private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder) : HttpMessageHandler` — captures `CallCount`, `LastRequestUri`, `LastRequestBody`.
  - `private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory` — `CreateClient(name) => new(handler)`.
  - `Build(StubHandler handler, ValhallaOptions? valhalla = null)` — constructs the provider with stubbed factory + default options.
  - **`GetLeg_TargetsConfiguredBaseUrl_WithTrailingSlashTrimmed`** (asserts `LastRequestUri == "http://valhalla.internal:9999/route"` for `BaseUrl = "http://valhalla.internal:9999/"`) — the single-host assertion to mirror for AC-1b.
  - **`GetLeg_AnyAir_MakesNoHttpCall_AndIsPlaceholder`** (asserts `handler.CallCount == 0` for Air) — the no-HTTP-for-Air assertion to mirror.
  The no-egress test should reuse this pattern (a fresh small test class is fine; if you prefer, place the new test in a dedicated `NoEgressTests.cs` and lift/share the stub shapes — do not duplicate the entire `ValhallaTravelTimeProviderTests` file).
- **The `[TRIP-MANUAL-01]` upsert guard** — `LucidCartographer/Services/Trip/TravelTimeComputationBackgroundService.cs`, `UpsertAsync` (lines ~241–291). The guard is:

  ```csharp
  // Never overwrite Manual or Measured rows (user-entered or higher-fidelity data).
  if (existing is not null && existing.Fidelity is Fidelity.Manual or Fidelity.Measured)
  {
      return;
  }
  ```

  Plus the read-side `IsUpgradeEligible(fidelity, source)` gate (lines ~199–201): `(fidelity is Fidelity.Estimated or Fidelity.Placeholder) && (source is TravelTimeSource.Mock or TravelTimeSource.EstimatedFallback)` — so Manual/Measured rows are never even re-enqueued. The NFR-10 test (AC-5) asserts this guard holds across the estimate→measured progression; it does **not** modify the guard.
- **The fidelity ladder renderer** — `LucidCartographer/Components/Shared/Trip/FidelityBadge.razor`. `Label(fidelity)` maps `Measured → UiStrings.TripFidelityMeasured`, `Estimated → UiStrings.TripFidelityEstimated`, `Manual → UiStrings.TripFidelityManual`, and **`_ => null`** (Placeholder/null render **no** pill — the time slot shows "—"). This is exactly the four-rung ladder FR-17 confirms; **no new label, no new tone, no new tier**. Covered by `LucidCartographer.Tests/Components/Trip/FidelityBadgeTests.cs` (keep green).

### The change — new no-egress test (AC 1, 2)

Add automated assertions (new test class, mirroring `ValhallaTravelTimeProviderTests`'s stub seam):

- **Mock half (AC-1a):** Build `MockTravelTimeProvider` with only `Options.Create(new TravelTimeOptions())` — **no** factory, **no** handler. Call `GetLegAsync` for Drive (and Walk/Cycle) and assert the result is `Fidelity.Estimated` with `DistanceMeters > 0` / `DurationSeconds > 0`. The proof is structural-plus-behavioral: the provider has no HttpClient dependency to call (constructor signature) **and** it returns a real estimate with no network present. Optionally add a guard assertion that the provider type's constructor parameters do not include `IHttpClientFactory`/`HttpClient` (reflection) to lock the "in-process" contract against future regressions — keep it simple and analyzer-clean.
- **Valhalla half (AC-1b):** Using `StubHandler` + `StubHttpClientFactory`, build the provider with a known `BaseUrl` (e.g. `http://valhalla.internal:9999`). For a Drive leg, assert `handler.CallCount == 1` and `handler.LastRequestUri!.Host` (and port) equals the configured host — i.e. the **only** host contacted is the configured one (no second host, no Geofabrik, no public router). For an AnyAir leg, assert `handler.CallCount == 0`. Frame the asserts/comments explicitly as the NFR7 no-egress check (single internal host; no per-route egress carrying coordinates).

### The change — new operator doc `docs/valhalla.md` (AC 3)

Mirror `docs/osrm.md`'s structure/tone (intro → privacy framing → "How it works" → numbered setup steps → upgrade existing estimates → turning it off). Adapt to Valhalla's **turnkey** model:

- Intro: Mock/smart-haversine is the shipping default (no infra); Valhalla is opt-in, per deployment, for **Measured** road durations/distances/geometry.
- **Privacy (NFR7):** Valhalla is self-hosted; stop coordinates never leave the deployment. The **only** outbound access is the build-time `.pbf` fetch from `tile_urls` (Geofabrik); routing requests stay on the internal compose network. Restate this as the hard guarantee. Mention the OSM/ODbL attribution obligation (NFR8, surfaced by the map).
- **How it works:** ONE engine, all ground modes via dynamic costing (Drive→auto, Walk→pedestrian, Cycle→bicycle); Air stays straight-line Placeholder; on first start / `.pbf` change it auto-downloads + auto-builds tiles, and during that window routing degrades to Estimated and self-heals to Measured once tiles finish (FR-13a).
- **Turnkey setup (the exact three steps from Story 2.5):** `docker compose --profile valhalla up`; set the region via `tile_urls` (the single knob — a Geofabrik `.osm.pbf`); set `TravelTime__Provider=Valhalla` (and optionally `TravelTime__Valhalla__BaseUrl=http://valhalla:8002`, which matches the default). No manual extract/partition/customize.
- **One-time tile-build cost (OQ-3 / NFR-9):** time / disk / RAM for a region build. If you can run a real `--profile valhalla up` smoke in this environment, record the measured numbers (cross-check the Story 2.5 Dev Agent Record for any figures already captured). If `docker` is unavailable (as it was for Story 2.5), document the published gis-ops/docker-valhalla guidance — e.g. tile build is a one-time, region-sized, RAM-heavy step (NFR-9 targets ~4–8 GB; build time minutes→tens of minutes by region; disk on the order of the `.pbf` plus built tiles) — and mark it `[ASSUMPTION]` / **operator-verify**. Do not present unmeasured numbers as measured.
- **Documented operator egress check (AC-3e, AD-5):** give the operator a concrete procedure to confirm no stop-coordinate egress during normal routing — e.g. while planning a trip (legs computing), observe that the `valhalla` container's only outbound connection was the one-time `.pbf` fetch and that per-route traffic stays on the internal compose network (e.g. `docker compose logs valhalla`, or inspecting container network connections / `docker stats` / a host-level `ss`/firewall observation). Frame it as a repeatable check, not a one-off.
- **Upgrade existing estimates / turning it off:** mirror `docs/osrm.md` §4 and "Turning it back off" (the existing recompute upgrades Estimated→Measured, never overwriting Manual/Measured; set `TravelTime__Provider=Mock` and drop `--profile valhalla` to revert).

Keep it operator-facing and accurate to what Stories 2.2–2.5 actually shipped. **Do not** delete or edit `docs/osrm.md`.

### The change — fidelity-ladder confirmation + NFR-10 no-downgrade test (AC 4, 5)

- **AC-4 is confirmation only:** the existing `FidelityBadge.razor` already renders the exact four-rung ladder (Measured / Estimated / Manual pills; Placeholder/null → "—", no pill). Confirm via the existing `FidelityBadgeTests` staying green. **No** new badge, **no** rendering change.
- **AC-5 NFR-10 no-downgrade test:** add a focused background-service test (mirror the existing `TravelTimeComputationBackgroundServiceTests` setup — `InternalsVisibleTo` exposes `UpsertAsync`/`ProcessOnceAsync`). Seed a `RouteSegment` with `Fidelity = Manual` and another with `Fidelity = Measured` (`Source = Valhalla`), then drive a measured-provider upsert/pass for those same keys and assert **both** rows are unchanged (duration, distance, fidelity, source all identical). This is the NFR-10 counter-metric: the estimate→measured progression never downgrades or deletes a protected row. Reuse the established test harness — do **not** add a parallel guard.

### What must NOT change

- **`docs/osrm.md`** — left in place verbatim; Epic 3 / Story 3.3 (FR-14) deletes it. This story **adds** `docs/valhalla.md` alongside it. (AC 3)
- **`FidelityBadge.razor` and all trip-view rendering** — FR-17 is reuse-only; no new badge type, label, tone, or tier. (AC 4)
- **`MockTravelTimeProvider` / `ValhallaTravelTimeProvider` / `TravelTimeComputationBackgroundService`** — the in-process estimation, single-host routing, and `[TRIP-MANUAL-01]` no-downgrade guard are all already built (Stories 1.2 / 2.2 / 2.3). The new tests **verify** them; they do not modify production code.
- **`docker-compose.yml`** — done in Story 2.5; not touched here.
- **OSRM artifacts** — provider/options/exception/DI branch/sidecars/`docs/osrm.md` removal is all Epic 3.

### Verified existing contracts (read before writing tests)

- **`MockTravelTimeProvider` ctor = `IOptions<TravelTimeOptions>` only** (`Services/Trip/MockTravelTimeProvider.cs`) — the structural no-HttpClient proof for AC-1a.
- **`ValhallaTravelTimeProvider.GetLegAsync` single `/route` POST to `BaseUrl`** (`Services/Trip/ValhallaTravelTimeProvider.cs` lines ~44–117) — Air returns before HTTP (lines ~50–55); the URI is `{BaseUrl.TrimEnd('/')}/route` (line ~65). The single-host + no-Air-HTTP behavior AC-1b asserts.
- **`StubHandler` / `StubHttpClientFactory` / `Build` / `GetLeg_TargetsConfiguredBaseUrl…` / `GetLeg_AnyAir_MakesNoHttpCall…`** (`LucidCartographer.Tests/Services/ValhallaTravelTimeProviderTests.cs` lines ~31–66, ~160–171, ~267–280) — the exact stub seam + assertion shapes to mirror.
- **`UpsertAsync` `[TRIP-MANUAL-01]` guard + `IsUpgradeEligible`** (`Services/Trip/TravelTimeComputationBackgroundService.cs` lines ~199–201, ~251–255) — the no-downgrade behavior AC-5's test asserts.
- **`FidelityBadge.razor` `Label`/`Tooltip`/`PillClass`** (`Components/Shared/Trip/FidelityBadge.razor`) — Measured/Estimated/Manual pills; Placeholder/null → no pill. The four-rung ladder FR-17 confirms.
- **`docs/osrm.md`** — the structure/tone template the new `docs/valhalla.md` mirrors (intro → privacy → How it works → numbered setup → upgrade → turn off).

## Constraints (NFRs)

- **NFR7 — Privacy (HARD CONSTRAINT).** Stop coordinates must never leave the deployment at any rung. Verified by AC-1: smart-haversine computes in-process (no HttpClient); Valhalla contacts only the one configured internal base-URL host (no per-route egress carrying coordinates); Air issues no HTTP. The doc (AC-3) restates the guarantee and gives the operator a repeatable egress check (AD-5).
- **AD-5 — no-egress containment + automated test + documented operator verification.** This story delivers all three: the containment is designed-in (Stories 2.2/2.5), the automated no-egress test is added here (AC-1/AC-2), and the operator-verification procedure is documented in `docs/valhalla.md` (AC-3).
- **FR-13 — operator documentation.** A new operator doc (`docs/valhalla.md`) replacing the **role** of `docs/osrm.md` (turnkey setup, region selection, one-time tile-build cost, privacy guarantee, operator egress check). `docs/osrm.md` is **not** deleted here (Epic 3 / FR-14).
- **FR-17 — coherent fidelity ladder, no new badge.** Estimated (default & fallback) / Measured (Valhalla) / Manual / Placeholder "—" using **existing** badges only. Confirmation, not a build; no rendering change.
- **NFR-10 — graceful degradation / no-downgrade counter-metric.** No Manual or Measured row is downgraded or deleted across the estimate→measured progression. Verified by the new NFR-10 no-downgrade test against the already-built `[TRIP-MANUAL-01]` guard.
- **NFR-9 — performance/footprint.** The doc records the one-time tile-build cost (time/disk/RAM, ~4–8 GB RAM target) measured where feasible (OQ-3), else `[ASSUMPTION]`/operator-verify.
- **NFR-12 — Build discipline.** New test code compiles clean under `TreatWarningsAsErrors` with no group-B analyzer violations (MA0002/MA0015/MA0046/MA0047/MA0074/VSTHRD200 — note `VSTHRD200` requires async test methods to be named `…Async`).
- **NFR-13 — DI seam integrity.** The Trip integration filter must pass after the change (recurring regression point), even though this story adds no DI change.
- **Additive / no regression.** Add new tests + one new doc only. No production C# change, no schema change, no EF migration, no compose change, no `docs/osrm.md` change, no badge change.

## Testing

This story's deliverable is **mostly test code + one doc**, verifying already-built behavior. Add only the tests that prove the new ACs; do **not** duplicate Story 2.2/2.3 coverage.

Automatable / required:

- **NFR7 no-egress test (AC 1, 2).**
  - *Mock issues no outbound HTTP:* build `MockTravelTimeProvider` with `Options.Create(new TravelTimeOptions())` and **no** HttpClient/factory; assert a Drive/Walk/Cycle leg returns `Fidelity.Estimated` with positive distance/duration computed in-process. (Structural proof: the provider's ctor has no `IHttpClientFactory`/`HttpClient` parameter — optionally lock with a reflection assertion.)
  - *Valhalla contacts only the configured host:* using the `StubHandler` + `StubHttpClientFactory` seam, build the provider with a known `BaseUrl` (e.g. `http://valhalla.internal:9999`); for a Drive leg assert `handler.CallCount == 1` and `LastRequestUri.Host`/port equals the configured host (no other host contacted); for an AnyAir leg assert `handler.CallCount == 0`. Mirror `GetLeg_TargetsConfiguredBaseUrl_WithTrailingSlashTrimmed` and `GetLeg_AnyAir_MakesNoHttpCall_AndIsPlaceholder`.
- **NFR-10 no-downgrade test (AC 5).** Seed a `RouteSegment` with `Fidelity = Manual` and one with `Fidelity = Measured` (`Source = Valhalla`); drive a measured-provider upsert/pass for those keys (the estimate→measured progression); assert **both** rows are unchanged (duration/distance/fidelity/source identical). Reuse the existing `TravelTimeComputationBackgroundServiceTests` harness (`InternalsVisibleTo` exposes `UpsertAsync`/`ProcessOnceAsync`); do not add a parallel guard.
- **Fidelity-ladder confirmation (AC 4).** No new test required beyond confirming the existing `FidelityBadgeTests` stay green (Measured/Estimated/Manual pills; Placeholder/null → "—"); **no** new badge.
- **Suites stay green (NFR-12, NFR-13).** `dotnet test --filter "FullyQualifiedName!~Integration"` (full fast suite incl. the new tests, `FidelityBadgeTests`, `ValhallaTravelTimeProviderTests`, `TravelTimeComputationBackgroundServiceTests`) and the Trip integration filter `--filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`. Build clean under `TreatWarningsAsErrors`.

Explicitly **manual / out of CI scope (OQ-3):**

- **Tile-build cost figures** (time / disk / RAM for a region) in `docs/valhalla.md` are an **operator/manual** measurement — multi-minute, region-sized, RAM-heavy — and are **not** a CI test. If `docker` is unavailable in this environment, the doc records the published gis-ops/docker-valhalla guidance marked `[ASSUMPTION]`/operator-verify (do not present unmeasured numbers as measured). The operator-verification egress procedure in the doc is likewise a manual check, not an automated one.

## Build/Test commands

- Fast suite (incl. new no-egress + no-downgrade tests, FidelityBadge, Valhalla, background-service): `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Trip integration (NFR-13 regression point): `dotnet test --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`
- Build clean check (NFR-12): `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Manual tile-build smoke (operator/OQ-3, not CI; for the doc's cost figures): `docker compose -f LucidCartographer/docker-compose.yml --profile valhalla up`

## Dev Notes

- **This is a test + doc story, not a production-code story.** All three behaviors are already built: in-process Mock estimation (Story 1.2 / `MockTravelTimeProvider`), single-host Valhalla routing + Air-skips-HTTP (Story 2.2 / `ValhallaTravelTimeProvider`), and the `[TRIP-MANUAL-01]` Manual/Measured no-downgrade guard (Story 2.3 / `TravelTimeComputationBackgroundService.UpsertAsync`). Add tests that **prove** them and a doc that **explains** the privacy/turnkey story. Do **not** modify production C#.
- **Reuse the existing stub seam.** `ValhallaTravelTimeProviderTests` already has `StubHandler` (captures `CallCount`/`LastRequestUri`), `StubHttpClientFactory`, and `Build(...)`, plus the two assertion shapes you need (`GetLeg_TargetsConfiguredBaseUrl…` for single-host, `GetLeg_AnyAir_MakesNoHttpCall…` for no-Air-HTTP). Mirror that pattern; do not hand-roll a new HTTP stub or stand up a real listener.
- **The Mock no-HttpClient proof is structural.** `MockTravelTimeProvider`'s primary constructor takes only `IOptions<TravelTimeOptions>` — there is no HttpClient to call. A behavioral "returns Estimated with no network" assertion plus (optionally) a reflection check on the ctor parameters locks NFR7 for the default rung against future regressions.
- **NFR-10 test reuses the background-service harness.** `TravelTimeComputationBackgroundServiceTests` already drives `UpsertAsync`/`ProcessOnceAsync` via `InternalsVisibleTo` with an in-memory/SQLite `AppDbContext`. Seed Manual + Measured rows, run the measured pass, assert both survive byte-for-byte. The guard returns early on `Fidelity is Manual or Measured` and the read-side `IsUpgradeEligible` never re-enqueues them — your test confirms both halves.
- **`docs/valhalla.md` mirrors `docs/osrm.md`.** Same section skeleton, adapted to the turnkey one-container/one-env model. Be accurate to what shipped (provider id `Valhalla`, `BaseUrl=http://valhalla:8002`, `--profile valhalla`, single `tile_urls` knob, Estimated-during-build self-healing to Measured). Restate NFR7 and give a concrete, repeatable operator egress check. **Do not delete `docs/osrm.md`** — that is Epic 3 / Story 3.3 / FR-14.
- **Tile-build cost (OQ-3).** If `docker` runs here, record measured time/disk/RAM (and check the Story 2.5 Dev Agent Record for figures already captured). If not, document the gis-ops/docker-valhalla published guidance and mark `[ASSUMPTION]`/operator-verify — never present unmeasured numbers as measured. NFR-9 RAM target is ~4–8 GB.
- **FR-17 is confirmation, not construction.** The four-rung ladder already renders via `FidelityBadge.razor`; do not add a badge type, label, tone, or visible tier. Confirm with the existing `FidelityBadgeTests` plus the NFR-10 no-downgrade test.
- **Analyzer discipline (NFR-12).** New async test methods must be named `…Async` (VSTHRD200); use `CultureInfo.InvariantCulture` for any string formatting; no unawaited tasks. The build is `TreatWarningsAsErrors`.

### Project Structure Notes

- New test(s): under `LucidCartographer.Tests/Services/` (alongside `ValhallaTravelTimeProviderTests.cs`) for the no-egress test, and the NFR-10 no-downgrade test alongside the existing `TravelTimeComputationBackgroundServiceTests` (wherever it lives in the test project). A dedicated `NoEgressTests.cs` is acceptable; reuse the stub shapes rather than copying the whole Valhalla test file.
- New doc: `docs/valhalla.md` (sibling of the retained `docs/osrm.md`).
- No production source, compose, or schema files are touched.

### References

- [Source: _bmad-output/planning-artifacts/epics.md#Story 2.6] — acceptance criteria
- [Source: _bmad-output/planning-artifacts/epics.md#Additional Requirements] — AD-5 (NFR7 containment design + automated no-egress test: default issues no outbound HTTP, Valhalla targets only the configured internal base URL; + documented operator verification), FR-13 (operator doc replacing `docs/osrm.md`), FR-17 (coherent two-badge ladder, no new badge type), NFR7 (privacy hard constraint), NFR-10 (no-downgrade counter-metric), NFR-9 (tile-build cost), OQ-3 (tile build time/disk/RAM measured in impl)
- [Source: _bmad-output/planning-artifacts/architecture.md] — AD-5 NFR7 verification; the off-circuit background compute service; the `[TRIP-MANUAL-01]` upsert guard
- [Source: LucidCartographer/Services/Trip/MockTravelTimeProvider.cs] — default provider computes in-process via `EstimatedTravelTime.Compute`; ctor takes only `IOptions<TravelTimeOptions>` (no HttpClient) — the structural NFR7 proof for the default rung (Story 1.2)
- [Source: LucidCartographer/Services/Trip/ValhallaTravelTimeProvider.cs] — single `/route` POST to the one configured `BaseUrl`; Air returns before any HTTP — the single-host + no-Air-HTTP behavior the no-egress test asserts (Story 2.2)
- [Source: LucidCartographer.Tests/Services/ValhallaTravelTimeProviderTests.cs] — the `StubHandler`/`StubHttpClientFactory`/`Build` stub seam and the `GetLeg_TargetsConfiguredBaseUrl_WithTrailingSlashTrimmed` (single-host) + `GetLeg_AnyAir_MakesNoHttpCall_AndIsPlaceholder` (no-Air-HTTP) assertion shapes to mirror
- [Source: LucidCartographer/Services/Trip/TravelTimeComputationBackgroundService.cs] — `UpsertAsync` `[TRIP-MANUAL-01]` no-downgrade guard (lines ~251–255) + `IsUpgradeEligible` read gate (lines ~199–201) — the NFR-10 behavior the no-downgrade test asserts (Story 2.3)
- [Source: LucidCartographer/Components/Shared/Trip/FidelityBadge.razor] — the four-rung ladder renderer (Measured/Estimated/Manual pills; Placeholder/null → "—"); FR-17 confirms it with no new badge
- [Source: docs/osrm.md] — the structure/tone the new `docs/valhalla.md` mirrors; **not** deleted here (Epic 3 / FR-14)
- [Source: _bmad-output/implementation-artifacts/stories/story-2-5-turnkey-docker-valhalla-compose-service-and-tile-build-window-degrade.md] — format template; Story 2.5 (done) shipped the compose service + the three-step turnkey enable this doc describes; check its Dev Agent Record for any captured tile-build figures

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (BMAD dev-story)

### Debug Log References

- `docker` is **not available** in this build environment (confirmed for Story 2.5 and unchanged). Per OQ-3/NFR-9, the tile-build cost figures (time/disk/RAM) in `docs/valhalla.md` could not be measured here, so they are documented as the published gis-ops/docker-valhalla guidance + the project NFR-9 footprint targets, explicitly marked `[ASSUMPTION]` / operator-verify, with a concrete measure-it-yourself procedure. No unmeasured number is presented as measured.
- C# build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug` → **Build succeeded, 0 Warning(s), 0 Error(s)** (clean under TreatWarningsAsErrors). No production C# changed — this is a test + doc story.
- Test project compiled clean under TreatWarningsAsErrors (no group-B analyzer violations: MA0002/MA0015/MA0046/MA0047/MA0074/VSTHRD200 — the new async test methods are named `…Async`).
- Fast suite: `dotnet test --filter "FullyQualifiedName!~Integration"` → **1040 passed, 0 failed** (was 1033; +7 new tests). Trip integration: `--filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"` → **20 passed, 0 failed**. New tests confirmed running (filter `NoEgress|NFR10` → 7 passed). `docs/osrm.md` left intact (98 lines, unmodified).

### Completion Notes List

This is a **test + doc** story. All three runtime behaviors were already built (Stories 1.2 / 2.2 / 2.3); this story **proves** them automatically and **documents** the privacy/turnkey story. **No production C# was modified.**

- **AC-1 / AC-2 — NFR7 automated no-egress test (AD-5):** Added `LucidCartographer.Tests/Services/NoEgressTests.cs`, mirroring the `ValhallaTravelTimeProviderTests` stub seam (`StubHandler` capturing `CallCount`/`LastRequestUri` + `StubHttpClientFactory`; no real network).
  - *Mock half (AC-1a):* `DefaultProvider_GroundLeg_ComputesInProcess_WithNoHttpClientInPlay_Async` builds `MockTravelTimeProvider` with **only** `Options.Create(new TravelTimeOptions())` — no factory, no handler — and asserts a Drive/Walk/Cycle leg returns a real `Estimated` result (positive distance/duration) computed in-process. `DefaultProvider_Ctor_HasNoHttpClientDependency_NFR7Structural` locks the structural contract via reflection: the ctor's only parameter is `IOptions<TravelTimeOptions>` (no `IHttpClientFactory`/`HttpClient`), so the default provider is physically incapable of an out-call.
  - *Valhalla half (AC-1b):* `Valhalla_GroundLeg_ContactsOnlyTheConfiguredHost_NoOtherEgress_Async` asserts a Drive leg POSTs **once** and the captured `LastRequestUri.Host` **and** `.Port` equal the configured `BaseUrl` (`http://valhalla.internal:9999`) with path `/route` — the only host contacted is the single configured internal one. `Valhalla_AirLeg_MakesNoHttpCall_NoEgress_Async` asserts `CallCount == 0` for AnyAir (no request, no coordinate egress). Comments frame the asserts explicitly as the NFR7 no-egress check.
- **AC-3 — operator doc `docs/valhalla.md`:** Created, mirroring `docs/osrm.md` structure/tone. Covers: (a) turnkey 3-step enable (`docker compose --profile valhalla up`, set `tile_urls`, set `TravelTime__Provider=Valhalla`); (b) region selection via the single `tile_urls` knob → a Geofabrik `.osm.pbf`; (c) one-time tile-build cost — **not measurable here (no docker)**, so documented as gis-ops/docker-valhalla guidance + NFR-9 targets (~4–8 GB RAM, minutes→tens of minutes, disk ≈ `.pbf` + tiles), explicitly `[ASSUMPTION]`/operator-verify with a measure-it-yourself procedure (`docker compose logs`/`docker stats`/`du`); (d) the NFR7 privacy guarantee restated (only the build-time `.pbf` fetch leaves; per-route traffic is internal-only; Mock computes in-process); (e) a repeatable operator egress check (`docker compose logs valhalla`, in-container `ss -tnp`/host-level `ss`, internal-only port option). All figures verified against the shipped compose service (image `:3.5.1`, port 8002, `tile_urls`/`use_tiles_ignore_pbf`/`build_tar`, volume `/custom_files`, `/status` healthcheck). **`docs/osrm.md` NOT deleted/edited** (Epic 3 / Story 3.3 / FR-14 owns that).
- **AC-4 — fidelity-ladder confirmation (FR-17), confirmation only:** No production rendering change and **no new badge type**. The existing `FidelityBadge.razor` already renders the four-rung ladder (Measured/Estimated/Manual pills; Placeholder/null → "—", no pill). Confirmed by the existing `FidelityBadgeTests` staying green (Measured/Estimated/Manual + Placeholder/null cases) plus the NFR-10 no-downgrade test below.
- **AC-5 — NFR-10 no-downgrade test:** Added `ProcessOnce_EstimateToMeasuredProgression_NeverDowngradesManualOrMeasuredRows_NFR10` to the existing `TravelTimeComputationBackgroundServiceTests` (reusing its `InternalsVisibleTo` harness — `ProcessOnceAsync`/`SeedDriveOpenPath`/`MeasuredStubProvider`). It seeds **both** a pre-existing Manual row and a pre-existing Measured (`Source=Valhalla`) row, runs a measured-capable pass (the estimate→measured progression), and asserts both rows survive byte-for-byte (duration/distance/fidelity/source unchanged) and neither is deleted (row count = 2). This exercises the already-built `[TRIP-MANUAL-01]` upsert guard + `IsUpgradeEligible` read-gate; **no new guard code**.
- **AC-6 — automatable validation:** New no-egress + NFR-10 tests green; existing FidelityBadge/Valhalla/background-service tests green; build clean under `TreatWarningsAsErrors` (no group-B analyzer violations); Trip integration filter green. Tile-build figures are operator/manual (OQ-3), not a CI test.

### File List

- `LucidCartographer.Tests/Services/NoEgressTests.cs` — **new**: NFR7 no-egress test (AD-5). Mock computes in-process with no HttpClient (behavioral + reflection-structural); Valhalla contacts only the single configured host for a ground leg and makes no HTTP for Air.
- `LucidCartographer.Tests/Services/TravelTimeComputationBackgroundServiceTests.cs` — **modified**: added the NFR-10 no-downgrade test (`ProcessOnce_EstimateToMeasuredProgression_NeverDowngradesManualOrMeasuredRows_NFR10`) asserting a pre-existing Manual row and a pre-existing Measured row both survive the estimate→measured progression byte-for-byte.
- `docs/valhalla.md` — **new**: turnkey operator doc (3-step setup, `tile_urls` region selection, one-time tile-build cost `[ASSUMPTION]`/operator-verify, NFR7 privacy guarantee, repeatable operator egress check). Mirrors `docs/osrm.md`; `docs/osrm.md` left intact (Epic 3 owns its removal).

### Change Log

| Date       | Change |
|------------|--------|
| 2026-06-24 | Story drafted (create-story): NFR7 no-egress verification (Mock issues no outbound HTTP; Valhalla contacts only the configured host) + operator doc `docs/valhalla.md` + fidelity-ladder confirmation (FR-17, no new badge) + NFR-10 no-downgrade test. Status → ready-for-dev. |
| 2026-06-24 | Senior Developer Review (AI): Approved. All 6 ACs verified against production contracts; 0 CRITICAL/HIGH/MEDIUM, 2 LOW (non-blocking). Build clean (0 warn/0 err); fast suite 1042/1042; Story-2.6 tests 9/9; FidelityBadge 11/11; Trip integration 20/20; `docs/osrm.md` intact; no production C# changed. Status → done. |
| 2026-06-24 | dev-story: added `NoEgressTests.cs` (NFR7/AD-5: default provider in-process + no-HttpClient structural proof; Valhalla single-host + no-Air-HTTP); added NFR-10 no-downgrade test (Manual+Measured both survive estimate→measured progression byte-for-byte); created `docs/valhalla.md` (turnkey 3-step setup, `tile_urls` region knob, tile-build cost `[ASSUMPTION]`/operator-verify, NFR7 guarantee + operator egress check). FR-17 confirmation only — no badge/rendering change; `docs/osrm.md` untouched; no production C# changed. Build clean (0 warn/0 err under TreatWarningsAsErrors); fast suite 1040/1040; Trip integration 20/20. docker unavailable → tile-build figures documented as guidance, not measured (OQ-3). Status → review. |

## Senior Developer Review (AI)

**Reviewer:** satec\yurik (autonomous story-automator review)
**Date:** 2026-06-24
**Outcome:** ✅ **Approved** — Story 2.6 (final story of Epic 2) meets all six acceptance criteria. No production code change was required or made; this is a test + doc story proving already-built behavior.

### Scope reviewed
Story 2.6's surface only (per review brief): `LucidCartographer.Tests/Services/NoEgressTests.cs` (new), `LucidCartographer.Tests/Services/TravelTimeComputationBackgroundServiceTests.cs` (NFR-10 test added), `docs/valhalla.md` (new). Intermingled uncommitted Epic 1 / Stories 2.1–2.5 changes in the working tree were explicitly excluded.

### AC verification (each cross-checked against production contracts)
- **AC-1a / NFR7 (default no egress):** `DefaultProvider_GroundLeg_ComputesInProcess...` asserts `Estimated` + positive distance/duration across Drive/Walk/Cycle with no factory/handler in scope; `DefaultProvider_Ctor_HasNoHttpClientDependency_NFR7Structural` reflection-locks the ctor to `IOptions<TravelTimeOptions>` only. Matches `MockTravelTimeProvider` (ctor takes only `IOptions<TravelTimeOptions>`; `GetLegAsync` is in-process). **VERIFIED.**
- **AC-1b / NFR7 (Valhalla single host):** `Valhalla_GroundLeg_ContactsOnlyTheConfiguredHost...` asserts `CallCount==1` and captured URI host+port+path `/route` equal the configured `BaseUrl` across all three ground modes; `Valhalla_AirLeg_MakesNoHttpCall...` asserts `CallCount==0` for AnyAir. Matches `ValhallaTravelTimeProvider.GetLegAsync` (Air returns before any HTTP, lines 50–55; single `/route` POST to `BaseUrl`, lines 64–73). **VERIFIED.**
- **AC-2 (stub seam, no real network):** Reuses the `StubHandler` (`CallCount`/`LastRequestUri`) + `StubHttpClientFactory` pattern; Mock half proves "no HttpClient" structurally. **VERIFIED.**
- **AC-3 / FR-13 (operator doc):** `docs/valhalla.md` covers (a) turnkey 3-step enable, (b) `tile_urls` region knob → Geofabrik `.osm.pbf`, (c) one-time tile-build cost (time/disk/RAM) correctly marked `[ASSUMPTION]`/operator-verify with a measure-it-yourself procedure (docker unavailable — no unmeasured number presented as measured), (d) NFR7 privacy guarantee restated, (e) repeatable operator egress check. `docs/osrm.md` confirmed present and unmodified (Epic 3 owns its removal). **VERIFIED.**
- **AC-4 / FR-17 (fidelity ladder, no new badge):** Confirmation only; no rendering change. `FidelityBadgeTests` 11/11 green. **VERIFIED.**
- **AC-5 / NFR-10 (no downgrade):** `ProcessOnce_EstimateToMeasuredProgression_NeverDowngradesManualOrMeasuredRows_NFR10` seeds Manual + Measured rows, runs a measured-capable pass, asserts both byte-for-byte intact (duration/distance/fidelity/source) and row count == 2. Exercises the existing `[TRIP-MANUAL-01]` guard (line 252) + `IsUpgradeEligible` read-gate (line 199); no new guard code. **VERIFIED.**
- **AC-6 (automatable validation):** Build clean (0 warn/0 err under TreatWarningsAsErrors); fast suite 1042/1042 (delta vs the dev record's 1040 is intermingled out-of-scope tests in the working tree); Story-2.6 tests 9/9; FidelityBadge 11/11; Trip integration 20/20. **VERIFIED.**

### Findings
- **CRITICAL: 0 · HIGH: 0 · MEDIUM: 0**
- **LOW (non-blocking, no fix applied):**
  - The Valhalla ground-mode theory reuses one `OkBody` for all modes — proves single-host containment per costing token but not the per-mode token (covered separately by `GetLeg_Success_ReturnsMeasured_WithCostingToken`). Acceptable.
  - The Mock-half theory name references "no HttpClient in play" but the structural absence is asserted in the companion reflection test; the pair together satisfies AC-1a. Acceptable.

### Auto-fixes applied
None — no CRITICAL/HIGH/MEDIUM issues found; LOW items are intentional and not worth code churn.

### Decision
0 CRITICAL issues → **Status: done**. Sprint-status `2-6-nfr7-no-egress-verification-operator-doc-and-fidelity-ladder-confirmation` → done. Epic 2 complete.

_Reviewer: satec\yurik on 2026-06-24_
