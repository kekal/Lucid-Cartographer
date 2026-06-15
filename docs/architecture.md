# Architecture

_LucidCartographer — Blazor Server (.NET 8) full-stack monolith._

## Executive Summary

A self-hosted Blazor Server app for POI management. The UI is server-rendered (InteractiveServer over SignalR), state lives in per-page ViewModels, business logic is organized into interface-first vertical service slices, and persistence is EF Core + SQLite accessed through a context factory. Background services handle the long-running work (enrichment, deduplication, import, export) off the request/circuit thread. Two AI/automation surfaces exist: a Playwright-driven Google Maps integration and an MCP server.

## Layering

```
Components (.razor)        thin view hosts: markup, bindings, ~12-line lifecycle bridge
      │  injects
      ▼
ViewModels (*ViewModel.cs) Transient; all UI state; orchestrate services;
      │  calls               event Action? StateChanged; IAsyncDisposable; own a CTS
      ▼
Services (vertical slices) interface-first: Import / Enrichment / Operations /
      │  uses                Auth / Export / Mcp / Browser + root services
      ▼
Data (EF Core / SQLite)   AppDbContext via IDbContextFactory; Fluent API + constraints
```

- **Components** hold no business logic. The `@code` block subscribes to `Vm.StateChanged` in `OnInitializedAsync`, marshals via `InvokeAsync(StateHasChanged)`, and unsubscribes + disposes the VM in `DisposeAsync`. Heavier pages also expose `RendererDispatch = InvokeAsync` so background/Rx callbacks marshal onto the renderer.
- **ViewModels** are registered `Transient` (one per page-component instance — WPF "new VM per window" semantics; Scoped would wrongly share across navigations within a circuit).
- **Services** never injected `AppDbContext` directly — they take `IDbContextFactory<AppDbContext>` and create short-lived contexts (`await using`). DI lifetimes are deliberate: singletons for shared state (locks, caches, OAuth keys, background services), scoped for per-request contexts/matchers, transient for stateless page VMs.

## Composition Root

`Program.cs` only wires things up. Registrations live in `Configuration/*Extensions.cs` (13 extension methods: Razor+compression, database, POI services, import/enrichment/dedup pipelines, browser session, export, auth, resilience, view models, MCP, OAuth frontdoor, health). Endpoints live in `Endpoints/*Endpoints.cs`. One-shot startup work lives in `Services/StartupCleanupService.cs` (migrate DB, seed admin, sweep temp files, revive stuck POIs, vacuum sessions).

### Middleware order (security-critical)
ForwardedHeaders (**first** — rewrites `X-Forwarded-Proto`) → exception handler/HSTS/HTTPS redirect (non-dev, ARCH-HIGH-05) → security headers/CSP (ARCH-CRIT-04) → response compression (after headers, BREACH-safe, ARCH-HIGH-06; skipped in dev) → WebSockets → Authentication → Authorization → LAN-bypass-or-auth → Antiforgery (ARCH-CRIT-03) → rate limiter → static files → endpoints.

## Service Slices

- **Import/** — `IFileImporter` impls (GPX, KML/KMZ, GeoJSON, CSV) + `ImportOrchestrator` (dispatch, coord validation, multi-folder KML → per-folder collections, signals `EnrichmentTrigger` once after commit) + Coravel queue (`ImportInvocable`). Google Maps lists arrive via `IGoogleMapsListScraper` (Playwright).
- **Enrichment/** — `PoiEnrichmentBackgroundService` (`BackgroundService`) polls the queue / `EnrichmentTrigger`, fans a batch across workers via `Channel<int>`, page concurrency via `SemaphoreSlim`, wrapped in the Polly `enrichment` pipeline (3 retries, jittered backoff, 2-min per-attempt timeout). `PoiDetailEnricher` (static) does the actual Google Maps page scrape. `EnrichmentStateMachine` (pure) decides outcomes.
- **Operations/** — `SetOperationService` (subtract/intersect/union/dedup with spatial tolerance), `PoiMatcher` (identity: stable place-id first, then name-similarity via Fastenshtein + Haversine proximity; transitive duplicate grouping), `PoiDeduplicationService` + `PoiDeduplicationBackgroundService` (whole-DB dedup on startup, on `DedupTrigger`, and hourly).
- **Auth/** — `PasswordHasher` (PBKDF2-SHA256, 600k iterations, iteration count embedded in hash), `SessionStore` (hashed opaque tokens, 30-day lifetime, fixed-time comparison).
- **Export/** — `IFileExporter` impls (GPX, KML with HTML balloons / category folders), `GoogleMapsListExporter` (UI automation to push a saved list), `ExportBackgroundService` (single-consumer `Channel` queue so a long headful run never blocks imports).
- **Browser/** — `BrowserSessionManager` (single long-lived persistent-profile Chromium, lazy init behind a `SemaphoreSlim`, per-page CDP mobile emulation), `GoogleBrowserLock` (single-flight), `GoogleSignIn`/`GoogleConsent` helpers.
- **Trip/** — the **Trip Planning** slice (additive lens over a `PoiCollection`). `ITravelTimeProvider` (Mock haversine default / optional OSRM) is the single travel-data gateway; `RouteSegment` is the directional `(From,To,Mode)`-keyed per-pair Leg/Distance-Matrix cache; `TravelTimeComputationBackgroundService` mirrors enrichment (poll/`TravelTimeTrigger`, per-worker DbContext, `SqliteWriteLock`, Polly `travel-time` pipeline; **ground-only** auto-compute — Any/Air legs are never estimated); `TripOrderingService` is the single writer of 1-based `OrderIndex` **and** the per-leg `OutgoingTravelMode` (drag/keyboard/TSP/MCP/mode-pill all funnel through it); `TspSolver` (pure NN+2-opt) over a **mode-invariant** `DistanceMatrixService` matrix does assisted ordering; `ItineraryTimeline` + `TravelTimeFormatting.DisplayMinutes` produce the **reconciled round-once display model** (lowest-fidelity-wins; displayed total == Σ displayed legs). **Wave 2** added per-leg travel modes end-to-end, the desktop Trip-View takeover, and a multi-day schedule. Decisions tagged `TRIP-*`. See [trip-planning.md](./trip-planning.md).
- **Mcp/** — read/write/enrichment tool types plus `TripTools` (read trip with **per-leg** travelMode, assign Stop Order, **set per-leg travel mode**, set Start/Finish/dwell) exposed at `/mcp`.

## Cross-Cutting Concerns

- **Concurrency control:** `SqliteWriteLock` (singleton) serializes all `SaveChangesAsync` between enrichment, dedup, and trip-time/order writes (SQLite single-writer); `GoogleBrowserLock` serializes browser ops (one Chromium profile/process); `Interlocked` disposed-flags and circuit guards protect JS interop during prerender/teardown.
- **Event-driven background work:** `EnrichmentTrigger` and `DedupTrigger` wake workers; Coravel + Channel queues decouple UI from long jobs.
- **Resilience:** Polly v8 named pipelines — `scraper` (concurrency 1 + retry + 10-min timeout), `enrichment` (retry + 2-min timeout), `travel-time` (wraps provider calls in the trip compute service).
- **Responsive UI:** `ViewportService` (scoped, one per circuit, ~768px breakpoint, cookie-seeded during SSR to avoid flash) drives a desktop/mobile component split on every page.
- **JS interop:** `leafletInterop.js` (map layers, markers, bounds, splitters, geolocation, downloads), plus `viewport.js`, `theme.js`, `history.js`, `reconnect.js`.

## Security Architecture

Cookie session auth for the local UI; optional OAuth 2.1 (OpenIddict) frontdoor for remote MCP connectors; LAN bypass for trusted private networks (requires `Auth:TrustedProxies`/`TrustedNetworks` behind a proxy or auth is silently bypassed); three-tier `/mcp` auth (LAN → API key → OAuth). Strict CSP, antiforgery on login, rate limiting, Data Protection keys persisted to the data volume. See [api-contracts.md](./api-contracts.md) and [deployment-guide.md](./deployment-guide.md).

## Testing Strategy

Three layers — **unit** (importers, exporters, orchestrators, matchers, ViewModels, state machine), **component** (bUnit), **integration** (`IntegrationTestBase`: a real `WebApplication` + Playwright + a temp SQLite DB per test, with `WebRootPath` pointed at the app's `wwwroot`). Mobile and desktop render paths have dedicated test bases. See [development-guide.md](./development-guide.md).

## Design Decision Codes

Comments reference codes — search the code for context: `ARCH-CRIT-*` (migrations, antiforgery, CSP), `ARCH-HIGH-*` (DI lifetimes, HTTPS defense-in-depth, header/compression ordering, scraper single-flight), `ARCH-LOW-*` (unobserved-task logging, Docker health), `HIGH-*` (concurrency/resource control), `MED-*` (compression, OS-independent paths, health, Docker perms), `IE-*` (import/enrichment), `OPS-R*` (set-operation rules), `REVIEW-*` (data-layer review fixes), `TRIP-*` (Trip Planning: `TRIP-CACHE-*`, `TRIP-ORDER-*`, `TRIP-OSRM-*`, `TRIP-TSP-*`, `TRIP-TIMELINE-*`, `TRIP-MCP-*`, and the Wave-2 codes `TRIP-LEGMODE-*` (per-leg mode; null ≡ AnyAir), `TRIP-RECONCILE-*` (round-once display), `TRIP-SCHEDULE-*` (finish-by computed once), `TRIP-MANUAL-*` (Manual fidelity sacrosanct), etc.).
