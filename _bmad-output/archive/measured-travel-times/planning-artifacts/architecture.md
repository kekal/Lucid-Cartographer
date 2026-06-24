---
stepsCompleted: [1, 2, 3, 4, 5, 6, 7, 8]
inputDocuments:
  - _bmad-output/planning-artifacts/prds/prd-maps_editor-2026-06-23/prd.md
  - _bmad-output/planning-artifacts/prds/prd-maps_editor-2026-06-23/addendum.md
  - _bmad-output/planning-artifacts/research/technical-travel-time-distance-estimation-research-2026-06-23.md
  - _bmad-output/project-context.md
workflowType: 'architecture'
project_name: 'maps_editor'
user_name: 'Yurik'
date: '2026-06-23'
lastStep: 8
status: 'complete'
completedAt: '2026-06-23'
---

# Architecture Decision Document — Measured Travel-Time & Distance Estimation

_LucidCartographer (self-hosted Blazor Server). Feature: smart-haversine default + self-hosted
Valhalla measured provider, replacing the hand-rolled OSRM path. This document is the single source
of truth for AI agents implementing the PRD dated 2026-06-23 (status: final)._

> **Brownfield note.** This is a delta inside an existing, mature codebase. The provider seam
> (`ITravelTimeProvider`), the off-circuit background service, the directional `RouteSegment` cache,
> the DI two-overload pattern, and the canonical-units discipline are **already built and must not be
> reworked**. Every decision below is bounded by the rules in
> [`project-context.md`](../project-context.md) — especially the `TRIP-*` design codes
> (`TRIP-DEGRADE-01`, `TRIP-MANUAL-01`, `TRIP-SCHEMA-01`, `TRIP-CACHE-01`) and the strict
> Component → ViewModel → Service → Data layering.

---

## Project Context Analysis

### Requirements Overview

**Functional Requirements (17, in 5 feature groups):**

- **Feature A — Smart-haversine default (FR-1…FR-4).** Apply a **per-mode detour/winding factor** to
  the great-circle distance before deriving duration from existing per-mode speeds. The upgrade lands
  in the **single existing estimate code path** (`EstimatedTravelTime.Compute`), which is reused by
  both `MockTravelTimeProvider` (FR-1/FR-3) **and** the background-service fallback (FR-4) — so one
  change satisfies both the default rung and the degrade rung, keeping `[TRIP-DEGRADE-01]` honest.
  Air/AnyAir stays Placeholder.
- **Feature B — Valhalla provider (FR-5…FR-10).** A new `ValhallaTravelTimeProvider` behind the
  unchanged seam, config-selected via `TravelTime:Provider=Valhalla`. One engine serves all ground
  modes via per-request dynamic costing (Drive→`auto`, Walk→`pedestrian`, Cycle→`bicycle`). Returns
  Measured duration (s) / distance (m) / road polyline; declares OSM/ODbL attribution; degrades to
  smart-haversine on any failure; never routes Air.
- **Feature C — Turnkey deployment (FR-11…FR-13a).** A single auto-building `docker-valhalla` compose
  service under a `valhalla` profile, replacing three OSRM sidecars + three prep passes. Operator
  enable = start profile + one env var (`tile_urls`) + `TravelTime:Provider=Valhalla`. The
  first-boot/tile-build window degrades to estimate, is operator-visible (log line + doc), and
  self-heals when Valhalla becomes reachable.
- **Feature D — OSRM removal & migration (FR-14…FR-16).** Delete all hand-rolled OSRM artifacts;
  a retired `Osrm` provider id warns + falls back (does not brick boot); existing `Source=OSRM` cache
  rows are invalidated once on migration so they recompute under the active provider.
- **Feature E — Fidelity ladder & badging (FR-17).** Two visible badges unchanged
  (Estimated / Measured), plus Manual and Placeholder. The smart upgrade improves accuracy *behind*
  the Estimated badge; **no new badge type**.

**Non-Functional Requirements that shape the architecture:**

- **NFR7 — Privacy (HARD CONSTRAINT, non-negotiable).** No stop-coordinate egress at any rung. Must
  be *designed-in and verified*, not asserted: default computes in-process; Valhalla targets the
  internal compose endpoint only; the `.pbf` is fetched at tile-build time only. This is the
  dominant architectural constraint and drives the containment design and an automated no-egress test.
- **NFR8 — Attribution.** Valhalla (OSM-derived) attribution surfaces on the map via the existing
  wiring; smart-haversine declares none. The OSRM attribution string is replaced, not removed.
- **NFR-10 — Reliability / graceful degradation.** One leg's failure (including the whole tile-build
  window) degrades to estimate and never fails the batch; the upsert never downgrades Manual/Measured.
- **NFR-12 — Build discipline / NFR-13 — DI seam integrity.** Clean under `TreatWarningsAsErrors` +
  Meziantou/VSTHRD analyzers (no group-B violations); the parameterless `AddTripServices()` registers
  the smart-haversine default, the `IConfiguration` overload adds Valhalla; the Trip integration
  filter must pass.

**Scale & Complexity:**

- Primary domain: **full-stack web (Blazor Server)** — but this delta is **operator-facing
  infrastructure** (providers/config/compose), not a UI feature. No new end-user workflow, no UX spec
  (correctly absent).
- Complexity level: **medium**. Low *surface* (one new provider class + options + exception + DI
  branch + one compose service + a one-time migration), but **high constraint density** — NFR7 is a
  hard privacy gate, and the recompute-trigger change is the one subtle architectural move.
- Net-new components: ~6 (Valhalla provider, options, exception, DI branch, compose service, one-time
  OSRM-row migration) + 1 modified estimate path + 1 broadened background-service trigger.

### Technical Constraints & Dependencies

- **External:** `docker-valhalla` (nilsnolde) image — pin, never `:latest`; Geofabrik `.pbf`
  (build-time only); OSM/ODbL licensing (NFR8, already wired).
- **Internal (reused unchanged):** the provider seam, `TravelTimeComputationBackgroundService` shape,
  the `RouteSegment` directional cache key `(FromPoiId, ToPoiId, TravelMode)` `[TRIP-CACHE-01]`, the
  `SqliteWriteLock` sole-writer gate, canonical units (s / m), Polly `"travel-time"` pipeline.

### Cross-Cutting Concerns Identified

1. **Privacy containment (NFR7)** — spans provider code, compose networking, deployment docs, tests.
2. **Graceful degradation (`[TRIP-DEGRADE-01]`)** — the smart-haversine path is *both* the default and
   the universal fallback; the tile-build window is just a long degrade.
3. **Cache fidelity protection (`[TRIP-MANUAL-01]`)** — Manual/Measured rows must never be downgraded
   or deleted by any new code path, including the FR-16 migration.
4. **Three-site leg-projection mirror** — `TripViewModel.BuildLegs`,
   `TravelTimeComputationBackgroundService.DirectionalPairs`, MCP `TripTools.GetTrip` stay in lockstep
   (unchanged by this feature, but the broadened recompute trigger touches the background site — verify
   no projection drift).
5. **DI seam integrity (NFR-13)** — the recurring integration-host regression point.

---

## Starter Template Evaluation

**Not applicable — brownfield delta.** No starter template is selected or initialized. This feature
extends an existing .NET 8 / C# 14 solution with a fixed, documented toolchain
([`project-context.md`](../project-context.md)):

- **Runtime/lang:** .NET 8.0 (`net8.0`), C# `LangVersion 14.0` (requires .NET 10 SDK to build).
- **UI:** Blazor Server `InteractiveServer`, Tailwind 3.4.17.
- **Data:** EF Core 8.0.27 + SQLite via `IDbContextFactory<AppDbContext>`.
- **Resilience/HTTP:** Polly 8.5 named pipeline `"travel-time"`; `IHttpClientFactory` named clients.
- **Tests:** xUnit 2.9, FluentAssertions 7, Moq 4.20, bUnit 1.36, EF Core InMemory, Playwright 1.49.

The "first implementation step" here is not a scaffold command — it is the smart-haversine change
(the lowest-risk, highest-coverage rung), sequenced in the Decision Impact Analysis below.

---

## Core Architectural Decisions

All open questions from PRD §10 are resolved here at their stated default leanings (the PRD is
`status: final` and these leanings survived adversarial review). Where the PRD defers a *value*
(detour factors, thresholds, tile cost), the decision is the **mechanism + a documented default**, with
the value tuned in implementation.

### Decision Priority Analysis

**Critical (block implementation):**

- **AD-1** Smart-haversine lives in `EstimatedTravelTime.Compute` (sole estimate edge); detour factors
  added to `TravelTimeOptions`.
- **AD-2** Recompute-trigger broadening + provider-capability gate (the one subtle move).
- **AD-3** Valhalla provider contract (costing map, units conversion, geometry precision, failure type).
- **AD-4** DI selection branch (Valhalla replaces OSRM) preserving the two-overload seam.
- **AD-5** NFR7 containment design + automated no-egress verification.

**Important (shape the architecture):**

- **AD-6** FR-16 one-time OSRM-row invalidation as a startup one-shot.
- **AD-7** Retired-provider-id → warn + fallback (not fail-fast).
- **AD-8** Compose `valhalla` profile + image pinning.
- **AD-9** Attribution string swap (NFR8).

**Deferred (explicitly out of scope, per PRD §4 / §11):**

- In-app admin/status UI for provider selection & tile-build progress (follow-on; OQ-8).
- Itinero in-process provider (fallback only; not built).
- External SaaS opt-in provider (NFR7 waiver; not built).
- Mobile trip-view control parity (tracked separately).

### Data Architecture

- **No schema change.** The `RouteSegment` entity, its directional key `[TRIP-CACHE-01]`, and the
  `Fidelity`/`TravelMode` string-constant + EF-check-constraint model `[TRIP-SCHEMA-01]` are reused
  verbatim. **No EF migration is added.** _(This is itself a decision: the feature is provider/config
  only.)_
- **`Fidelity`** (`Data/Entities/Fidelity.cs`) is **unchanged** — Valhalla legs reuse the existing
  `Measured` value. (The addendum's "add `Valhalla`" refers to the **Source** provenance string, not a
  new Fidelity. Confirmed: no new `Fidelity` member, so no check-constraint change.)
- **`TravelTimeSource`** (`Services/Trip/TravelTimeSource.cs`): **add** `public const string Valhalla =
  "Valhalla";` and **remove** `Osrm`. Provenance only; not constrained by a DB check.
- **Caching strategy:** unchanged write-once-per-key model, with the **trigger** broadened (AD-2) so a
  non-authoritative row (Estimated/EstimatedFallback) becomes eligible for an upgrade pass — never
  weakening the never-downgrade-Manual/Measured upsert guard (`[TRIP-MANUAL-01]`).

### AD-1 — Smart-haversine (Feature A)

- **Decision:** Add per-mode detour factors to `TravelTimeOptions` and apply them in the single
  `EstimatedTravelTime.Compute` path: `adjustedDistance = haversine × DetourFactorFor(mode)`, then
  `duration = adjustedDistance ÷ SpeedFor(mode)`. Both the **reported distance (m)** and **duration
  (s)** reflect the adjusted value (FR-3). Fidelity stays `Estimated`; AnyAir stays `Placeholder`.
- **Why one site:** `EstimatedTravelTime.Compute` is already the DRY estimate edge reused by
  `MockTravelTimeProvider` and the background fallback. Changing it satisfies FR-1 (default), FR-3
  (badging/units), and FR-4 (degrade-path parity) at once.
- **Defaults (OQ-1, `[ASSUMPTION]`):** Drive ×1.3, Cycle ×1.2, Walk ×1.15. Operator-configurable
  under `TravelTime` (FR-2). Tune/source empirically in implementation; expose in `appsettings.json`
  with documented defaults.
- **Critical guard (interaction with TSP-Sort, `[RD3]`):** the TSP cost matrix
  (`DistanceMatrixService`) **must keep using raw haversine**, never the detour-adjusted distance —
  ordering is mode-invariant and happens before per-leg modes exist. The detour factor is a
  **travel-time-estimate** concern only. Do **not** route the cost matrix through the adjusted value.

### AD-2 — Recompute trigger + provider capability gate (Feature B/C, FR-8/FR-13a/FR-16)

- **Problem:** today `LoadPendingLegsAsync` enqueues a leg **iff no cache row exists**. FR-13a (upgrade
  estimate→measured once Valhalla finishes building tiles) and FR-16 (recompute after OSRM-row
  invalidation) both need an *already-present, non-authoritative* row to be re-attempted.
- **Decision:** Broaden the pending-leg predicate from "no row exists" to **"no row exists OR the row is
  upgrade-eligible"**, where upgrade-eligible = `Fidelity ∈ {Estimated, Placeholder}` **and** `Source ∈
  {Mock, EstimatedFallback}` (i.e. **never** Manual/Measured). To prevent perpetual rework on Mock
  deployments (an estimate recomputed by Mock yields the same estimate forever), **gate the broadened
  arm on provider capability**: only include upgrade-eligible rows when the active provider can produce
  a higher fidelity.
- **Mechanism:** add a capability signal to `ITravelTimeProvider` — `bool ProducesMeasuredFidelity`
  (Mock = `false`, Valhalla = `true`). The background service includes upgrade-eligible legs **only
  when `provider.ProducesMeasuredFidelity`**. New-row legs are always enqueued (unchanged behavior).
  - _Alternative considered & rejected:_ a timestamp/“staleness” sweep — heavier, and re-introduces
    churn risk. The capability bool is minimal and self-documenting.
- **Upsert still guards:** `UpsertAsync` keeps the `Fidelity is Manual or Measured → return` guard
  unchanged (`[TRIP-MANUAL-01]`). The broadened *read* never weakens the protected *write*.
- **Mirror check:** the change is in `LoadPendingLegsAsync`/`DirectionalPairs` only; the leg *shape*
  (consecutive pairs + roundtrip closing leg) is untouched, so the three-site projection mirror
  (`BuildLegs` / `DirectionalPairs` / MCP `GetTrip`) stays aligned.

### AD-3 — Valhalla provider contract (FR-5…FR-10)

- **New files:** `Services/Trip/ValhallaTravelTimeProvider.cs`, `Services/Trip/ValhallaOptions.cs`,
  `Services/Trip/ValhallaRouteUnavailableException.cs` (analogue of the OSRM exception so the
  background catch degrades cleanly).
- **`Source`** = `TravelTimeSource.Valhalla`; **`Attribution`** = a new Valhalla ODbL string (AD-9);
  **`ProducesMeasuredFidelity`** = `true`.
- **Costing map:** Drive→`auto`, Walk→`pedestrian`, Cycle→`bicycle`. Single `/route` POST per leg.
  AnyAir is **not** routed (returns Placeholder without HTTP — mirror OSRM behavior).
- **Units at the edge (NFR-11):** parse `trip.summary.time` (seconds, round to int),
  `trip.summary.length` (**km → ×1000 → meters**), and the leg `shape` (encoded polyline). Convert
  km→m **at the provider boundary only**; never mid-layer.
- **Geometry precision:** Valhalla emits **polyline6** by default. The decision is to request/treat
  geometry as **precision 6** and make `ValhallaOptions.GeometryPrecision` default to `6` (OSRM used
  5 by default but configured 6). **The map decoder precision MUST match** — verify
  `LeafletMap`/`IMapService` polyline decode uses precision 6 for Valhalla geometry; this is a known
  trap. A Measured leg with missing/blank geometry must `throw ValhallaRouteUnavailableException`
  (same rule as OSRM: a null geometry would persist unchecked under the Measured guard).
- **HTTP:** named `IHttpClientFactory` client `"valhalla"` with a User-Agent and a per-request timeout
  from `ValhallaOptions.RequestTimeoutSeconds` (default 10). Timeouts/HTTP errors/`no-route` →
  `ValhallaRouteUnavailableException` → background degrade.
- **`ValhallaOptions`:** `BaseUrl` (default `http://valhalla:8002`), `RequestTimeoutSeconds` (10),
  `GeometryPrecision` (6). Bound from `TravelTime:Valhalla`. **One** base URL (one engine, all modes) —
  contrast OSRM's three per-profile URLs.

### AD-4 — DI selection (Feature B, NFR-13)

- **Decision:** In `Configuration/TripServicesExtensions.cs`, **replace** the `=="Osrm"` branch with a
  `=="Valhalla"` branch in the `AddTripServices(IConfiguration)` overload: bind
  `TravelTime:Valhalla`, register the named `"valhalla"` HttpClient, register
  `ValhallaTravelTimeProvider` as the active `ITravelTimeProvider`. The parameterless
  `AddTripServices()` overload **keeps registering `MockTravelTimeProvider`** (smart-haversine default)
  — what the integration host composes by hand.
- **Unknown/retired id (AD-7, FR-15, OQ-4):** any unrecognized `TravelTime:Provider` value (including
  the now-retired `Osrm`) falls through to the **Mock default** — the app must **not** fail to boot.
  Because this silently downgrades Measured→Estimated, emit a **prominent high-level startup warning**
  naming the offending value, and call it out in the migration/release note. Decision: **warn +
  fallback**, not fail-fast.
- **Mandatory check:** run `dotnet test --filter
  "FullyQualifiedName~Integration&FullyQualifiedName~Trip"` after this change (recurring regression
  point).

### AD-5 — NFR7 containment & verification (HARD CONSTRAINT)

- **Designed-in containment:**
  - The smart-haversine default computes **in-process** — no network at all.
  - The Valhalla provider issues requests **only** to the configured internal `BaseUrl`
    (`http://valhalla:8002` on the compose network). No public endpoint, no per-route external call.
  - The Valhalla container's **only** permitted outbound access is the **build-time `.pbf` fetch** from
    Geofabrik; routing requests never leave the compose network. Deployment guidance documents keeping
    egress closed beyond the build fetch, and pins the third-party image (the trust boundary).
- **Verification (OQ-9 — decision: both):**
  - **Automated:** a test asserting the active default provider issues **no outbound HTTP** for a leg
    (computes in-process), and that the Valhalla provider targets only the configured internal base URL
    (e.g. assert the request URI host == configured host; no other host is contacted). Place under the
    Trip provider unit tests.
  - **Documented:** an operator procedure in the new deployment doc (replacing `docs/osrm.md`) to
    confirm no stop-coordinate egress during normal routing.

### AD-6 — FR-16 one-time OSRM-row invalidation (Feature D)

- **Decision:** Add a **one-time startup migration** (in `Services/StartupCleanupService.cs`, the
  established home for one-shot startup work) that deletes every `RouteSegment` whose `Source` equals
  the literal string `"OSRM"`, under `SqliteWriteLock`, **never touching Manual rows**. Deleting the
  rows (rather than marking them) forces a clean recompute via the existing missing-row trigger and
  avoids weakening the never-downgrade-Measured guard.
- **Literal string, not constant:** because FR-14 removes `TravelTimeSource.Osrm`, the migration matches
  the **literal `"OSRM"`** — do not reference the deleted constant. Add a brief code comment noting why
  the literal is intentional.
- **Idempotent / self-retiring:** after the rows are gone the delete is a no-op; safe to leave in place
  (or guard behind a one-time marker if the team prefers — left to implementation, no-op cost is
  negligible). Log the count removed.
- **Reuse note:** `RouteSegmentInvalidationService.InvalidateRecomputableForCollectionAsync` already
  deletes non-Manual/non-Measured rows per collection — but FR-16 must purge **Measured** OSRM rows
  globally, which that method deliberately spares. Hence a dedicated `Source=="OSRM"` purge, not a reuse
  of the collection method.

### AD-8 — Compose & image pinning (Feature C, OQ-7)

- **Decision:** Add a single `valhalla` service under a `valhalla` compose profile (so default
  `docker compose up` starts none of it), **remove** the three `osrm-*` services and the commented OSRM
  env block, and replace the commented OSRM app-env block with a Valhalla one. Auto-download + auto-tile-build
  into a mapped volume (`./appdata/valhalla:/custom_files`), `tile_urls` env for region, expose
  `8002`.
- **Pinning (OQ-7 — decision: pin):** reference the image by an **immutable pin** — a specific released
  tag verified at implementation time, ideally combined with a `@sha256:` digest — **never `:latest`**.
  (The current upstream tag could not be definitively confirmed via search at authoring time; the
  implementer pins the exact verified tag/digest then. The *policy* is the decision; the *value* is
  pinned in implementation.) See [Sources](#sources).

### AD-9 — Attribution (NFR8)

- **Decision:** Replace the OSRM attribution string with a Valhalla ODbL string in `UiStrings.cs`
  (rename/repoint `TripRoutingAttributionOsm` → e.g. `TripRoutingAttributionValhalla`), wired through
  the unchanged `provider.Attribution` → `TripViewModel.RoutingAttributionHtml` → `MapPage` →
  `LeafletMap.SetRoutingAttributionAsync` → `IMapService` → Leaflet chain. Suggested text:
  `"Routing © Valhalla · Map data © OpenStreetMap contributors (ODbL)"`. Smart-haversine declares
  `null` (not OSM-derived), so no routing attribution shows on the default rung.

### Decision Impact Analysis

**Implementation sequence (lowest-risk first):**

1. **AD-1 smart-haversine** — self-contained, immediately improves the default + fallback rung; no infra.
2. **AD-9 + Source constant** — attribution string + `TravelTimeSource` add/remove (compile scaffolding).
3. **AD-3 Valhalla provider + options + exception** — net-new, isolated behind the seam.
4. **AD-2 recompute trigger + capability bool** — touches the background service; cover with unit tests.
5. **AD-4 DI branch swap** — then run the Trip integration filter.
6. **AD-6 one-time OSRM purge** — startup migration.
7. **AD-7 retired-id warning** — wire into the DI fallthrough.
8. **AD-8 compose** + **FR-14 deletions** (OSRM files/services/tests) + **AD-5 no-egress test** +
   **FR-13 docs** (replace `docs/osrm.md`).

**Cross-component dependencies:** AD-2 depends on AD-3's `ProducesMeasuredFidelity` (define the
interface member first). AD-6 depends on AD-3/AD-4 being active so recompute lands on Valhalla. AD-9
must land before/with FR-14 deletes the OSRM attribution reference (avoid a dangling `UiStrings` key).

---

## Implementation Patterns & Consistency Rules

These prevent two AI agents from implementing the same delta two different ways. They restate the
project's existing conventions **as they apply to this feature** plus the feature-specific rules.

### Naming Patterns

- **Provider classes:** `<Engine>TravelTimeProvider` (`ValhallaTravelTimeProvider`), `sealed`,
  primary-constructor DI, implementing `ITravelTimeProvider`. Mirror `OsrmTravelTimeProvider`'s shape.
- **Options classes:** `<Engine>Options` (`ValhallaOptions`), `sealed`, bound from
  `TravelTime:<Engine>` config subsection.
- **Exceptions:** `<Engine>RouteUnavailableException` (`ValhallaRouteUnavailableException`), thrown for
  unreachable / timeout / no-route / missing-geometry, caught by the background degrade path.
- **Source constants:** PascalCase const in `TravelTimeSource`; the **string value** is the provider id
  used in config (`"Valhalla"`).
- **Config keys:** `TravelTime:Provider`, `TravelTime:Valhalla:BaseUrl`,
  `TravelTime:Valhalla:RequestTimeoutSeconds`, `TravelTime:Valhalla:GeometryPrecision`, and per-mode
  detour factors under `TravelTime` (e.g. `TravelTime:DriveDetourFactor`). Compose env uses the
  double-underscore form (`TravelTime__Provider=Valhalla`).
- **Named HttpClient:** lowercase engine id (`"valhalla"`), exposed as a `const string HttpClientName`
  on the provider (mirror OSRM).

### Structure Patterns

- All new provider code lives in the existing `Services/Trip/` vertical slice. **No new top-level
  folder.** Components hold no provider logic (strict layering).
- DI registration **only** in `Configuration/TripServicesExtensions.cs`; never in `Program.cs`
  (composition root only).
- One-shot startup migration **only** in `Services/StartupCleanupService.cs`.
- Tests: provider unit tests in the Tests project mirroring `OsrmTravelTimeProviderTests.cs`
  (→ `ValhallaTravelTimeProviderTests.cs`); background-service trigger behavior in
  `TravelTimeComputationBackgroundServiceTests.cs`; no-egress assertion alongside provider tests. Use
  `InternalsVisibleTo` rather than widening visibility.

### Format Patterns

- **Canonical units fixed at the edge (NFR-11):** seconds + meters internally; convert Valhalla's
  km→m and any precision only inside the provider. No mid-layer conversion.
- **Rounding:** duration rounded to int seconds at the provider edge (mirror OSRM's
  `(int)Math.Round`). Display rounding stays the sole responsibility of
  `TravelTimeFormatting.DisplayMinutes` (`[TRIP-RECONCILE-01]`) — do not introduce a second rounding
  edge.
- **JSON parsing:** `System.Text.Json` with explicit `[JsonPropertyName]` DTOs (mirror OSRM provider);
  `PropertyNameCaseInsensitive = true`. Coordinates formatted with `CultureInfo.InvariantCulture`
  (comma-decimal locales would corrupt the request).
- **Coordinate order:** Valhalla `/route` takes `{lat, lon}` JSON locations (contrast OSRM's
  `lon,lat` URL order) — get this right in the request builder; it is a classic provider-swap bug.

### Process Patterns

- **Degradation (`[TRIP-DEGRADE-01]`):** every provider failure path degrades to
  `EstimatedTravelTime.Compute` with `Source = TravelTimeSource.EstimatedFallback`, logged at Warning,
  one leg at a time — never failing the batch. The tile-build window is just a sustained degrade.
- **No-downgrade guard (`[TRIP-MANUAL-01]`):** the upsert keeps `Fidelity is Manual or Measured →
  return`. New read-side broadening (AD-2) must never reach a Manual/Measured row.
- **Privacy default (NFR7):** no new code path may send coordinates off-box by default. Any provider
  that would (SaaS) is out of scope and forbidden here.
- **Logging:** structured logs with the existing message style; the tile-build window emits a clear
  operator-visible startup/health line (FR-13a). No PII / no coordinates beyond existing debug lines.

### Enforcement Guidelines

**All AI agents MUST:**

- Keep the smart-haversine change confined to `EstimatedTravelTime.Compute` + `TravelTimeOptions`; do
  **not** apply detour factors to the TSP cost matrix (`[RD3]`).
- Add `ProducesMeasuredFidelity` to `ITravelTimeProvider` and gate the broadened recompute arm on it.
- Match the **literal `"OSRM"`** string in the FR-16 purge (the constant is deleted).
- Make Valhalla geometry precision and the map decoder precision agree (6).
- Run the Trip integration filter after any DI/VM-ctor/schema-adjacent change.
- Build clean under `TreatWarningsAsErrors`; introduce no group-B analyzer violations
  (MA0002/MA0015/MA0046/MA0047/MA0074/VSTHRD200); no `ConfigureAwait(false)`.

**Anti-patterns to avoid:**

- Adding a new `Fidelity` member for Valhalla (reuse `Measured`).
- Adding a new visible badge (FR-17 — two-badge ladder).
- A second rounding edge, or converting km→m anywhere but the provider boundary.
- A per-route external call, a published Valhalla egress port, or `:latest` image tag.
- A "staleness timestamp" recompute sweep instead of the capability-gated upgrade arm.
- Failing to boot on a retired/unknown provider id (must warn + fall back).

---

## Project Structure & Boundaries

### Affected & New Files (delta only — not a full tree)

```
LucidCartographer/
├── Services/Trip/
│   ├── ITravelTimeProvider.cs               # MODIFY: add `bool ProducesMeasuredFidelity`
│   ├── EstimatedTravelTime.cs               # MODIFY: apply per-mode detour factor (smart-haversine)
│   ├── TravelTimeOptions.cs                 # MODIFY: add per-mode detour factors (+ SpeedFor unchanged)
│   ├── MockTravelTimeProvider.cs            # MODIFY: ProducesMeasuredFidelity => false (estimate via Compute)
│   ├── TravelTimeSource.cs                  # MODIFY: add Valhalla const; remove Osrm const
│   ├── TravelTimeComputationBackgroundService.cs  # MODIFY: broaden pending-leg trigger (AD-2), capability gate
│   ├── ValhallaTravelTimeProvider.cs        # NEW: measured provider (auto/pedestrian/bicycle costing)
│   ├── ValhallaOptions.cs                   # NEW: BaseUrl, RequestTimeoutSeconds, GeometryPrecision=6
│   ├── ValhallaRouteUnavailableException.cs # NEW: degrade-path exception
│   ├── OsrmTravelTimeProvider.cs            # DELETE (FR-14)
│   ├── OsrmOptions.cs                        # DELETE (FR-14)
│   └── OsrmRouteUnavailableException.cs      # DELETE (FR-14)
├── Configuration/
│   └── TripServicesExtensions.cs            # MODIFY: replace Osrm branch with Valhalla branch + retired-id warn
├── Services/
│   └── StartupCleanupService.cs             # MODIFY: one-time purge of Source=="OSRM" RouteSegments (FR-16)
├── (UiStrings.cs)                           # MODIFY: OSRM attribution string -> Valhalla ODbL string (NFR8)
├── appsettings.json                         # MODIFY: TravelTime detour-factor defaults; Valhalla section
└── docker-compose.yml                       # MODIFY: remove 3 osrm-* services + OSRM env; add valhalla profile service

LucidCartographer.Tests/
├── .../ValhallaTravelTimeProviderTests.cs   # NEW: mirror OsrmTravelTimeProviderTests; + no-egress assertion (NFR7)
├── .../TravelTimeComputationBackgroundServiceTests.cs  # MODIFY: upgrade-trigger + capability-gate cases; drop OSRM refs
└── .../OsrmTravelTimeProviderTests.cs       # DELETE (FR-14)

docs/
├── osrm.md                                  # DELETE → REPLACE with turnkey-valhalla operator doc (FR-13)
└── (valhalla.md or measured-routing.md)     # NEW: setup, region selection, tile-build cost, privacy guarantee
```

### Architectural Boundaries

- **Provider boundary:** `ITravelTimeProvider` is the only seam between the background compute loop and
  any routing engine. Valhalla slots behind it; nothing upstream (VM, components, MCP) knows the engine.
- **Network boundary (NFR7):** app ⇄ Valhalla over the internal compose network only; Valhalla ⇄
  Geofabrik only at tile-build time. No other egress.
- **Write boundary:** `RouteSegment` writes go through `TravelTimeComputationBackgroundService.UpsertAsync`
  (and the one-time startup purge) under `SqliteWriteLock`. `OrderIndex`/`OutgoingTravelMode` writers
  are untouched by this feature.
- **Config boundary:** all tunables under `TravelTime:*`; provider selection is config/env-only (no
  admin UI — deferred).

### Requirements → Structure Mapping

| Feature | FRs | Primary locations |
|---|---|---|
| A — Smart-haversine | FR-1…4 | `EstimatedTravelTime.cs`, `TravelTimeOptions.cs`, `appsettings.json` |
| B — Valhalla provider | FR-5…10 | `Valhalla*` (provider/options/exception), `TripServicesExtensions.cs`, `ITravelTimeProvider.cs` |
| C — Turnkey deploy | FR-11…13a | `docker-compose.yml`, `docs/` operator doc, FR-13a log line in provider/background |
| D — OSRM removal/migration | FR-14…16 | `Osrm*` deletes, `TripServicesExtensions.cs`, `StartupCleanupService.cs`, compose, docs, tests |
| E — Fidelity ladder | FR-17 | No code change beyond reusing `Measured`/`Estimated`; `FidelityBadge.razor` unchanged |
| NFR7 / NFR8 | — | no-egress test + compose networking + docs; `UiStrings.cs` attribution |

### Data Flow

1. Trip View enabled on a collection → `TravelTimeComputationBackgroundService` wakes (trigger/idle poll).
2. `LoadPendingLegsAsync` builds directional ground legs lacking a cache row **or** upgrade-eligible
   (when `provider.ProducesMeasuredFidelity`).
3. Each leg → Polly `"travel-time"` pipeline → `provider.GetLegAsync`. Valhalla → internal HTTP →
   measured s/m/polyline. On failure → `EstimatedTravelTime.Compute` (smart-haversine, EstimatedFallback).
4. `UpsertAsync` writes under `SqliteWriteLock`, never downgrading Manual/Measured.
5. VM projects legs (`BuildLegs`) → `FidelityBadge` shows Measured/Estimated/Manual/—; map shows
   geometry + Valhalla ODbL attribution when active.

---

## Architecture Validation Results

### Coherence Validation ✅

- **Decision compatibility:** All decisions reuse the existing seam, cache, units, DI pattern, and
  degrade/guard rules — no contradictions with `project-context.md`. The one new interface member
  (`ProducesMeasuredFidelity`) is additive and implemented by both providers.
- **Pattern consistency:** Naming, options, exception, HttpClient, and JSON-DTO patterns mirror the
  existing OSRM provider being removed — net structural familiarity, not novelty.
- **Structure alignment:** Everything lands in `Services/Trip/`, `Configuration/`, and
  `StartupCleanupService` per the slice/composition-root rules.

### Requirements Coverage Validation ✅

- **All 17 FRs** mapped to concrete files (table above). FR-17 needs no new code (two-badge reuse).
- **NFRs:** NFR7 (containment + automated no-egress test + doc), NFR8 (attribution swap), NFR-10
  (degrade + guard preserved), NFR-11 (units at edge), NFR-12 (build discipline), NFR-13 (two-overload
  seam + integration filter) all addressed.

### Implementation Readiness Validation ✅

- Critical decisions documented with mechanisms and defaults; the one subtle move (AD-2) is specified
  with its rejected alternative and its capability gate. Sequence and cross-dependencies are explicit.

### Gap Analysis Results

**Critical gaps:** none open — all OQ items resolved at their PRD default leanings.

**Important (value-tuning, deferred to implementation by the PRD, not blockers):**

- OQ-1 detour-factor values (mechanism decided; defaults documented; tune empirically).
- OQ-2 SM-3 accuracy thresholds T₁/T₂ (set during implementation against a reference set).
- OQ-3 tile build time/disk/RAM for the operator's region (measure; document in FR-13/NFR-9).
- OQ-7 exact pinned image tag/digest (policy decided = pin; value pinned at implementation; upstream
  tag not confirmable via search at authoring time — see Sources).

**Nice-to-have (explicitly deferred per PRD §11):** in-app provider/tile-status UI; Itinero; SaaS
opt-in; mobile control parity.

### Architecture Completeness Checklist

**Requirements Analysis**

- [x] Project context thoroughly analyzed
- [x] Scale and complexity assessed
- [x] Technical constraints identified
- [x] Cross-cutting concerns mapped

**Architectural Decisions**

- [x] Critical decisions documented with versions
- [x] Technology stack fully specified
- [x] Integration patterns defined
- [x] Performance considerations addressed

**Implementation Patterns**

- [x] Naming conventions established
- [x] Structure patterns defined
- [x] Communication patterns specified
- [x] Process patterns documented

**Project Structure**

- [x] Complete directory structure defined
- [x] Component boundaries established
- [x] Integration points mapped
- [x] Requirements to structure mapping complete

### Architecture Readiness Assessment

**Overall Status:** READY WITH MINOR GAPS — all 16 checklist items confirmed and no Critical Gaps
remain; the open items are **empirical value-tuning** the PRD deliberately defers to the implementation
phase (detour factors, accuracy thresholds, tile-build cost, exact image pin), not architectural gaps.

**Confidence Level:** High — the seam, cache, units, degrade/guard rules, and DI pattern are
pre-existing and battle-tested; the delta is small, isolated, and mirrors the OSRM code it replaces.

**Key Strengths:**

- One estimate edge serves both default and fallback (smart-haversine lands once).
- NFR7 is designed-in (in-process default + internal-only routing + build-time-only `.pbf`) and
  verified by an automated no-egress test, not asserted.
- The subtle recompute change is contained and churn-safe via an explicit provider-capability gate.
- No schema/migration, no new badge, no new layer — minimal blast radius.

**Areas for Future Enhancement:** in-app admin/status UI (provider + tile-build), Itinero docker-free
option, consented SaaS provider, mobile trip-view parity.

### Implementation Handoff

**AI Agent Guidelines:** Follow these decisions exactly; respect every `TRIP-*` rule in
`project-context.md`; build clean under the analyzer regime; run the Trip integration filter after the
DI change.

**First Implementation Priority:** AD-1 — add per-mode detour factors to `TravelTimeOptions` and apply
them in `EstimatedTravelTime.Compute` (improves the default + fallback rung with zero infrastructure),
then proceed down the Decision Impact sequence.

---

## Sources

- [nilsnolde/docker-valhalla (GitHub)](https://github.com/nilsnolde/docker-valhalla) — turnkey
  auto-download/auto-build Valhalla container; pin a specific released tag/digest at implementation
  time (not `:latest`).
- [docker-valhalla container packages (ghcr.io)](https://github.com/nilsnolde/docker-valhalla/pkgs/container/docker-valhalla%2Fvalhalla)
  — registry for verifying the exact pin (OQ-7).
- [Valhalla routing docs](https://valhalla.github.io/valhalla/) — `/route` API, costing models
  (auto / pedestrian / bicycle), polyline6 geometry.
