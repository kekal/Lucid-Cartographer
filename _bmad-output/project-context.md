---
project_name: 'maps_editor'
user_name: 'Yurik'
date: '2026-06-16'
sections_completed: ['technology_stack', 'language_rules', 'framework_rules', 'testing_rules', 'code_quality_rules', 'workflow_rules', 'critical_rules', 'trip_planning_rules']
existing_patterns_found: 14
status: 'complete'
rule_count: 32
optimized_for_llm: true
---

# Project Context for AI Agents

_This file contains critical rules and patterns that AI agents must follow when implementing code in this project (LucidCartographer — a self-hosted Blazor Server app for managing geographic points-of-interest). Focus on unobvious details that agents might otherwise miss._

---

## Technology Stack & Versions

- **Runtime:** .NET **8.0** (`net8.0`) — but **C# `LangVersion` is pinned to `14.0`**, which requires the **.NET 10 SDK** to compile. The pin is deliberate (not `latest`); a mismatched SDK fails loudly with CS9202. Bump `Directory.Build.props`, the app `.csproj`, **and** the Dockerfile build-stage SDK together. `LangVersion` is re-declared in the app `.csproj` because the Docker build context excludes `Directory.Build.props`.
- **UI:** Blazor Server, `@rendermode InteractiveServer`. Tailwind CSS **v3.4.17** (standalone CLI auto-downloaded into `obj/` by MSBuild — no Node.js; keep version in sync with the Dockerfile).
- **Data:** EF Core **8.0.27** + SQLite, accessed via `IDbContextFactory<AppDbContext>`.
- **Key libraries:** Coravel 6.0.2 (background queues), CsvHelper 33.0.1, NetTopologySuite GeoJSON/GPX, SharpKml.Core 6.1, Polly 8.5 (resilience/rate-limiting), Microsoft.Playwright 1.49 (scraper + integration tests), ModelContextProtocol.AspNetCore 1.3, OpenIddict 7.5 (OAuth 2.1 frontdoor), Fastenshtein (Levenshtein), Geolocation.
- **Tests:** xUnit 2.9, FluentAssertions 7, Moq 4.20, bUnit 1.36, EF Core InMemory.

## Critical Implementation Rules

### Build & Language Discipline
- `TreatWarningsAsErrors=true`, `Nullable=enable`, `ImplicitUsings=enable`, `EnforceCodeStyleInBuild=true`. A warning **is** a build break.
- Analyzers: Meziantou + Microsoft.VisualStudio.Threading. The `NoWarn` list has two groups — group A (permanent design choices) and group B (**baseline-suppressed legacy violations**). **New code MUST NOT introduce any group-B violation** (e.g. `MA0002`, `MA0015`, `MA0046`, `MA0047`, `MA0074`, `VSTHRD200`).
- Don't add `ConfigureAwait(false)` — `MA0004` is suppressed on purpose; Blazor Server needs the circuit's sync context.

### Architecture Layering (strict)
- **Component (`.razor`) → ViewModel → Service → Data.** Never skip a layer; components hold markup/bindings only.
- ViewModels: one per heavy page, `sealed`, **primary-constructor DI**, registered **`Transient`** (in `Configuration/ViewModelExtensions.cs`), expose `event Action? StateChanged` + a private `Notify()`, state with `private set`. Own a `CancellationTokenSource` and implement `IAsyncDisposable` where needed.
- The component `@code` block is a ~12-line bridge only: subscribe `Vm.StateChanged += OnVmChanged` in `OnInitializedAsync`, `OnVmChanged() => InvokeAsync(StateHasChanged)`, unsubscribe + dispose the VM in `DisposeAsync`.
- `Program.cs` is a **composition root only**. DI registrations live in `Configuration/*Extensions.cs`; minimal-API endpoints in `Endpoints/*Endpoints.cs`; one-shot startup work in `Services/StartupCleanupService.cs`.
- Services are vertical slices (`Import/`, `Enrichment/`, `Operations/`, `Auth/`, `Export/`, `Trip/`), **interface-first**.
- **DI seam may need two overloads.** A slice whose VM-facing services must also work in the integration host registers a **parameterless** overload (VM-facing, no self-firing loop / no resilience-pipeline / no `IConfiguration` dependency — what `IntegrationTestBase` composes by hand) **plus** an `IConfiguration` overload that calls the parameterless one then adds the production-only pieces (config-selected provider, hosted services, `Polly` pipelines). Pattern: `AddTripServices()` vs `AddTripServices(IConfiguration)` in `Configuration/TripServicesExtensions.cs`. The parameterless overload is the **recurring integration-host regression point** — see Workflow Rules.

### UI Conventions
- **No hardcoded UI text** — all strings go through `UiStrings` (`@UiStrings.*`).
- Large lists use `<Virtualize>` with `@key`. Status regions use `aria-live`; buttons/links carry `aria-label`. Styling is Tailwind utility classes with the project's `surface-*` / `on-surface-*` / `primary` token palette.
- Desktop and mobile are distinct render paths (`Viewport.IsMobile` → `Mobile*Screen`); update both when changing a page's UI.

### Testing Rules
- Three layers: **Unit** (pure logic — importers, exporters, orchestrators, ViewModels), **Component** (bUnit), **Integration** (`IntegrationTestBase`: real `WebApplication` + Playwright + a temp SQLite db per test, points `WebRootPath` at the app's `wwwroot`).
- `InternalsVisibleTo("LucidCartographer.Tests")` is set — test internals directly rather than widening visibility.
- Mobile vs desktop paths have dedicated bases/tests (`MobileTestBase`, `Mobile*Tests`) — cover both when touching responsive UI.
- **After any Trip DI / VM-constructor / hosted-service change, run the Trip integration filter** — the integration host composes services by hand via the parameterless `AddTripServices()` overload, so a VM ctor that gains a production-only dependency boots in `Program.cs` but breaks the A3 host. Filter: `dotnet test … --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`.

### Trip Planning Slice (`Services/Trip/`, `Components/Shared/Trip/`)
- **Canonical units are fixed at the edges, never converted mid-layer:** travel time in **seconds** (`RouteSegment.DurationSeconds`, provider results), distance in **meters** (`DistanceMeters`), dwell & time-budget in **minutes** (`PoiCollectionItem.DwellMinutes`, `PoiCollection.TimeBudgetMinutes`). Convert only at UI/provider boundaries.
- **The `RouteSegment` Leg cache key `(FromPoiId, ToPoiId, TravelMode)` is DIRECTIONAL** ([TRIP-CACHE-01]): A→B and B→A are distinct rows, never collapsed/mirrored (one-way streets make Drive legs genuinely asymmetric).
- **`TravelMode` and `Fidelity` are string constants** (`Data/Entities/TravelMode.cs`, `Fidelity.cs`), persisted as strings and constrained by EF **check constraints** built from each type's `.All` list via `EnumCheckSql` ([TRIP-SCHEMA-01]) — add a value to `.All` and the DB constraint follows automatically. Don't introduce int-backed enums for these.
- **One ordering write-path:** `TripOrderingService.SetOrderAsync` is the **sole writer** of `PoiCollectionItem.OrderIndex`, committing under the shared process-wide `SqliteWriteLock` (same gate as enrichment/dedup). Registered `Scoped`. Never write `OrderIndex` elsewhere.
- **Per-leg travel mode (the leg leaving each stop):** `PoiCollectionItem.OutgoingTravelMode` (string?, one of `TravelMode.All`) — **`null` ≡ AnyAir is ONE state** ([TRIP-LEGMODE-01]); never add an "unset" sentinel. It is **also sole-written by `TripOrderingService`** (under `SqliteWriteLock`): `SetOutgoingTravelModeAsync` for a single leg, and `SetOrderAsync` nulls it **only for stops whose successor changed** on a reorder (so unchanged legs keep their mode + cached row). A reorder that flips the trip shape (Set/Clear Finish appears/vanishes the closing leg) must pass the **prior** shape so the resurrected/vanished leg's mode resets — a `null` prior Finish is a real roundtrip shape, not "unsupplied". The per-leg mode replaces the old trip-wide `PoiCollection.TravelMode` as the leg driver; that column is **kept as a dead column** (RD1a — still written by the inert mobile `TravelModeSelector`), never dropped. Ground modes (Walk/Drive/Cycle) auto-compute; **AnyAir/null is never auto-estimated** (reads "—") — the background pass enqueues ground legs only.
- **Round-once display model ([TRIP-RECONCILE-01]):** `TravelTimeFormatting.DisplayMinutes` (round-half-up) is the **sole rounding edge**. The displayed trip total == the sum of the displayed per-leg minutes, and arrivals derive from the same rounded legs — never truncate each leg while summing seconds for the total. Canonical seconds are untouched; honesty qualifiers ("—", Estimated/Measured/Manual, partial-trip em-dash) survive.
- **Three leg-projection sites must stay mirrored:** `TripViewModel.BuildLegs`, `TravelTimeComputationBackgroundService.DirectionalPairs`, and MCP `TripTools.GetTrip` each build the leg set (consecutive pairs + roundtrip closing leg), decide open-vs-roundtrip shape, and look the cache up by the leg's own `(From,To,Mode)` key. Change one → change all three. The MCP `get_trip` reports each leg's `travelMode` (trip-level field removed); `set_leg_travel_mode` sets a leg by its From-stop id via the sole-writer.
- **TSP-Sort is mode-invariant ([RD3]):** the cost matrix is built from straight-line/haversine distance, never per-leg `OutgoingTravelMode` (ordering happens before per-leg modes exist). The NN+2-opt algorithm is unchanged.
- **Schedule conversions happen only at the UI edge:** `PoiCollection.TripStartTime` (`DateTime?`) and `TimeBudgetMinutes`/`DwellMinutes` stay canonical; the component bridge converts `datetime-local` (**ISO/invariant wire value**) and HH:MM↔minutes, and renders dates **locale-driven** (`CultureInfo.CurrentCulture`, no hard-coded order). A finish-by **deadline is computed once** into `TimeBudgetMinutes` (`deadline − start`) and never stored/recomputed ([TRIP-SCHEDULE-01]). The **Limit and Finish-by inputs are two views of the one canonical `TimeBudgetMinutes`** (a linked pair — editing one recomputes the other; never store both independently).
- **HH:MM duration entry goes through the shared `Components/Shared/Trip/DurationInput.razor`** (masked text field + ▲▼ steppers) for every duration picker — dwell, time-limit, and per-leg movement (`LegConnector`). It is **presentational**: it owns no state, holds canonical **minutes** via `[Parameter] int? Value`, and raises `ValueChanged` only. **The minutes⇄"HH:MM" conversion is centralized in `TravelTimeFormatting.FormatHhmm`/`TryParseHhmm`** — the SOLE such edge; don't reimplement HH:MM parsing/formatting elsewhere. Hours are **UNCAPPED** (a duration is not a clock time; >24h is valid) — there is no 24h wrap and no AM/PM; callers clamp to their own `Max`. **No JS interop** in the stepper: `Shift` is read straight off the Blazor mouse/keyboard event (Shift+click / Shift+ArrowUp = ±`ShiftStep`), and a committed edit bumps a `_rev` `@key` to force Blazor to re-render the canonical display (the controlled-input gotcha). The control `stopPropagation`s click/keydown so editing never selects or reorders the trip row (the 2.2 selection lesson).
- **Provider seam + haversine fallback:** travel times come through `ITravelTimeProvider` (haversine `MockTravelTimeProvider` is the default — OSRM is opt-in via `TravelTime:Provider=Osrm`, never default). The off-circuit `TravelTimeComputationBackgroundService` runs providers through the Polly `"travel-time"` pipeline and, on any provider failure, **degrades to the haversine Estimated value stamped `Source=EstimatedFallback`** ([TRIP-DEGRADE-01]) — one bad leg never fails the pass. A provider declares its own `Attribution` HTML; when an OSM-derived provider (OSRM) is active its OSM/ODbL attribution **must** surface on the map (NFR8) — haversine declares null (not OSM-derived).
- **Validate the never-invalidated cache row at write time.** A Leg is computed iff no cache row exists for its key; the upsert still defends with an explicit guard that **never downgrades a `Manual` or `Measured` row** ([TRIP-MANUAL-01]/[TRIP-DEGRADE-01]) and `RouteSegmentInvalidationService` never deletes `Manual` rows. Keep these guards even when the current code path "can't" reach them.

### Conventions Agents Miss
- **DB path resolution order:** `DB_PATH` env var → `Database:Path` config → `data/cartographer.db` under `ContentRootPath`.
- **Design-decision comment codes** in source — search the codebase before changing flagged code: `ARCH-CRIT-*`, `ARCH-HIGH-*`, `ARCH-LOW-*`, `HIGH-*`, `MED-*`, `IE-*`, and the Trip slice's `TRIP-*` codes (e.g. `TRIP-CACHE-01`, `TRIP-SCHEMA-01`, `TRIP-TRAVELTIME-01`, `TRIP-OSRM-01`, `TRIP-DEGRADE-01`, `TRIP-MANUAL-01`, and the Wave-2 codes `TRIP-LEGMODE-01`, `TRIP-RECONCILE-01`, `TRIP-SCHEDULE-01`).
- **Known tech-debt / deferred:** **A11** — a per-leg `Manual` override `RouteSegment` row is orphaned when the leg's mode is later changed (harmless to display since projection keys by current mode; fix = delete/migrate the old-mode Manual row in `SetOutgoingTravelModeAsync`). **Mirror-to-mobile is deferred** — `MobileTripPanel` still has the pre-Wave-2 controls (number dwell/budget, `type="time"` start, the inert trip-wide `TravelModeSelector`) and lacks the per-leg pill / connector edit / new schedule pickers; the **shared logic/data/strings already reach mobile correctly**, only the controls are deferred, so keep shared-layer changes mobile-correct and run the mobile test filter.
- **Auth:** PBKDF2-SHA256 @ 600,000 iterations; admin bootstrap prints a one-time password to the log. `Auth:BypassLocalAddresses` requires `Auth:TrustedProxies` behind a reverse proxy, or auth is silently bypassed for all requests.
- `BlazorDisableThrowNavigationException=true` is intentional — don't "fix" navigation exceptions.

---

## Usage Guidelines

**For AI Agents:**
- Read this file before implementing any code.
- Follow ALL rules exactly as documented; when in doubt, prefer the more restrictive option.
- Update this file if new durable patterns emerge.

**For Humans:**
- Keep this file lean and focused on agent needs.
- Update when the technology stack or core patterns change.
- Review periodically and remove rules that become obvious over time.

Last Updated: 2026-06-16
