---
baseline_commit: da4b8882dc712d36259447726126bcea11e1c153
---

# Story 2.4: Config/DI selection of the Valhalla provider

Status: done

## Story

As a deployment operator,
I want to select Valhalla with a single config value,
So that I can switch the running deployment to measured routing without code changes.

## Acceptance Criteria

1. **Given** `AddTripServices(IConfiguration)` in `LucidCartographer/Configuration/TripServicesExtensions.cs` currently branches on `string.Equals(providerId, "Osrm", StringComparison.OrdinalIgnoreCase)` (where `providerId = configuration["TravelTime:Provider"]`), **When** I replace that `=="Osrm"` branch with a `=="Valhalla"` branch, **Then** the new branch (a) binds `ValhallaOptions` from `configuration.GetSection("TravelTime:Valhalla")`, (b) registers the named `"valhalla"` `IHttpClientFactory` client (`ValhallaTravelTimeProvider.HttpClientName`) setting `client.Timeout` from `ValhallaOptions.RequestTimeoutSeconds` (the timeout wiring noted deferred in Story 2.2), and (c) registers `ValhallaTravelTimeProvider` as the active `ITravelTimeProvider` singleton (AD-4). The `else` arm still registers `MockTravelTimeProvider`.
2. **And** the parameterless `AddTripServices()` overload still registers `MockTravelTimeProvider` (the smart-haversine default the integration host composes by hand) — that overload is **unchanged** (NFR-13). The `IConfiguration` overload calls `AddTripServices()` first, then re-registers the config-selected provider last so it wins resolution.
3. **And** with `TravelTime:Provider=Valhalla` set, the active `ITravelTimeProvider` is `ValhallaTravelTimeProvider`, and its ODbL attribution (`provider.Attribution → UiStrings.TripRoutingAttributionValhalla`) surfaces on the map via the existing **unchanged** chain `provider.Attribution → TripViewModel.RoutingAttributionHtml → MapPage → LeafletMap` (FR-10, NFR8).
4. **And** with the **default** active (no `TravelTime:Provider`, an empty value, or any non-`Valhalla` value — `MockTravelTimeProvider`), no routing attribution shows: `MockTravelTimeProvider.Attribution` is `null`, so `RoutingAttributionHtml` is `null` and the map shows no routing attribution control content (FR-10, NFR8).
5. **And** a `TravelTime:Valhalla` section with documented defaults (`BaseUrl=http://valhalla:8002`, `RequestTimeoutSeconds=10`, `GeometryPrecision=6`) is added to `appsettings.json`, mirroring the existing `//`-prefixed comment-key documentation style used by the current `TravelTime:Osrm` block. The `//Provider` comment is updated to mention `Valhalla`. (The OSRM config block is **left in place** — its removal is Epic 3 / FR-14.)
6. **And** the Trip integration test filter passes: `dotnet test --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"` (NFR-13, recurring regression point — the integration host composes the parameterless overload, exercising the Mock/default path).
7. **And** the solution compiles clean under `TreatWarningsAsErrors` with no group-B analyzer violations (MA0002/MA0015/MA0046/MA0047/MA0074/VSTHRD200), and both the fast suite and the Trip integration filter stay green (NFR-12, NFR-13).

## Architecture & Code Context

This is **AD-4** — the config/DI selection step in Epic 2. Stories 2.1, 2.2, 2.3 are **done**: the `ProducesMeasuredFidelity` capability seam, `ValhallaTravelTimeProvider` / `ValhallaOptions` / `ValhallaRouteUnavailableException`, the `"valhalla"` HttpClient name constant, the Valhalla `Source` constant, the `UiStrings.TripRoutingAttributionValhalla` string, and the capability-gated recompute trigger all already exist. **This story does the one thing left to make Valhalla actually selectable: flip the DI branch and document the config.** It is a near-mechanical mirror of the existing OSRM branch, swapping types and the named client.

The change is **two files**: one production edit (`TripServicesExtensions.cs`) plus one config-doc edit (`appsettings.json`). **No new types, no schema change, no behavior change to the parameterless overload, no touching the provider/options/exception classes** (those are done). Do **not** remove the OSRM branch or OSRM config block — Epic 3 (FR-14) owns OSRM deletion; this story only **adds** the Valhalla branch alongside it (replacing OSRM as the recognized provider id, but leaving the dead OSRM code present-but-unreachable until Epic 3).

### Current state — `TripServicesExtensions.cs` (READ THIS FIRST)

Both overloads in full as they stand today:

```csharp
/// <summary>
/// VM-facing services: ordering, cache invalidation, travel-time signals, and a mock provider.
/// Excludes the hosted compute service and active provider so tests can compose in isolation.
/// </summary>
public static IServiceCollection AddTripServices(this IServiceCollection services)
{
    services.TryAddSingletonWriteLock();

    services.AddScoped<IDistanceMatrixService, DistanceMatrixService>();
    services.AddScoped<ITripOrderingService, TripOrderingService>();
    services.AddScoped<IRouteSegmentInvalidationService, RouteSegmentInvalidationService>();

    services.AddSingleton<TravelTimeTrigger>();
    services.AddSingleton<TravelTimeProgressService>();

    // Mock provider (haversine): default for tests. Has no Attribution and no external deps.
    // Production overload re-registers after this, so config-selected provider (e.g., OSRM) wins.
    services.AddSingleton<ITravelTimeProvider, MockTravelTimeProvider>();
    return services;
}

/// <summary>
/// Full production wiring: adds VM-facing services plus hosted compute service and Polly pipeline.
/// </summary>
public static IServiceCollection AddTripServices(
    this IServiceCollection services, IConfiguration configuration)
{
    services.AddTripServices();

    // Select provider by config: default is Mock (haversine), only "Osrm" swaps in OSRM provider.
    var providerId = configuration["TravelTime:Provider"];
    if (string.Equals(providerId, "Osrm", StringComparison.OrdinalIgnoreCase))
    {
        services.Configure<OsrmOptions>(configuration.GetSection("TravelTime:Osrm"));

        var timeoutSeconds = configuration.GetValue<int?>("TravelTime:Osrm:RequestTimeoutSeconds") ?? 10;
        services.AddHttpClient(OsrmTravelTimeProvider.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds));
            c.DefaultRequestHeaders.UserAgent.ParseAdd("LucidCartographer/1.0 (+osrm-routing)");
        });

        services.AddSingleton<ITravelTimeProvider, OsrmTravelTimeProvider>();
    }
    else
    {
        services.AddSingleton<ITravelTimeProvider, MockTravelTimeProvider>();
    }

    services.AddHostedService<TravelTimeComputationBackgroundService>();
    services.Configure<TravelTimeOptions>(configuration.GetSection("TravelTime"));
    return services;
}
```

The **only** production-logic edit is replacing the `=="Osrm"` branch body with a `=="Valhalla"` branch body. Everything else (`AddTripServices()` parameterless, `TravelTimeOptions` registration, `AddHostedService`, `TryAddSingletonWriteLock`) stays.

### The change — replace the branch (the only production edit)

Swap the `if` condition and its body to Valhalla. Keep the `else` (Mock) and the trailing lines exactly:

```csharp
// Select provider by config: default is Mock (smart-haversine), only "Valhalla"
// swaps in the measured Valhalla provider. Valhalla is opt-in, never the default.
var providerId = configuration["TravelTime:Provider"];
if (string.Equals(providerId, "Valhalla", StringComparison.OrdinalIgnoreCase))
{
    // Bind Valhalla options and register the named "valhalla" HttpClient.
    // Self-hosted (coordinates never egress), so no egress guard needed.
    services.Configure<ValhallaOptions>(configuration.GetSection("TravelTime:Valhalla"));

    var timeoutSeconds = configuration.GetValue<int?>("TravelTime:Valhalla:RequestTimeoutSeconds") ?? 10;
    services.AddHttpClient(ValhallaTravelTimeProvider.HttpClientName, c =>
    {
        c.Timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds));
    });

    services.AddSingleton<ITravelTimeProvider, ValhallaTravelTimeProvider>();
}
else
{
    services.AddSingleton<ITravelTimeProvider, MockTravelTimeProvider>();
}
```

Notes on the swap:
- **Named client + timeout (AC 1b).** Use `ValhallaTravelTimeProvider.HttpClientName` (the `"valhalla"` constant, Story 2.2) — do not hardcode the string. Read the timeout the same way the OSRM branch does (`GetValue<int?>("TravelTime:Valhalla:RequestTimeoutSeconds") ?? 10`, clamped with `Math.Max(1, …)`). **This closes the "RequestTimeoutSeconds wiring deferred" note from Story 2.2** — Story 2.2 added the `RequestTimeoutSeconds` option but did not wire it into the client; this branch wires it. The Valhalla provider creates its client via `httpClientFactory.CreateClient(HttpClientName)`, so `client.Timeout` set here is the per-request timeout the provider relies on (its timeout-exception path throws `ValhallaRouteUnavailableException`).
- **No `User-Agent` header.** The OSRM branch adds a `User-Agent` (`LucidCartographer/1.0 (+osrm-routing)`) because public OSRM demo servers require one. Valhalla is self-hosted on the internal compose network and needs no such header — omit it. (If you prefer symmetry you may add a `(+valhalla-routing)` UA; it is harmless but not required. Keep the branch minimal.)
- **Registration order (AC 2).** `AddTripServices()` (called first) registers `MockTravelTimeProvider`; the branch then registers the active provider **after**, so the last `AddSingleton<ITravelTimeProvider, …>` wins when `GetRequiredService<ITravelTimeProvider>()` resolves a single instance. This is the existing pattern — do not change it.
- **`Configure<TravelTimeOptions>` stays.** The trailing `services.Configure<TravelTimeOptions>(configuration.GetSection("TravelTime"));` is unchanged — `TravelTimeOptions` (speeds + detour factors) is needed by both providers (Valhalla's Air/AnyAir path and the degrade path both call `EstimatedTravelTime.Compute(..., travelTimeOptions.Value)`).
- **Imports.** `ValhallaOptions` and `ValhallaTravelTimeProvider` live in `LucidCartographer.Services.Trip`, already imported (`using LucidCartographer.Services.Trip;` at the top of the file). No new `using` needed.

### The config edit — add `TravelTime:Valhalla` to `appsettings.json` (AC 5)

The `TravelTime` section currently documents `Provider` and an `Osrm` sub-section with `//`-prefixed comment keys. Add a sibling `Valhalla` sub-section in the same documented style. Place it adjacent to the `Osrm` block (after the `Osrm` object, before `AssumedSpeedMetersPerSecond`, or wherever keeps the diff clean). Use the exact `ValhallaOptions` defaults (`BaseUrl=http://valhalla:8002`, `RequestTimeoutSeconds=10`, `GeometryPrecision=6`):

```jsonc
"//Valhalla": "Valhalla provider settings (used only when Provider=Valhalla). One self-hosted engine serves all ground modes via dynamic costing (Drive->auto, Walk->pedestrian, Cycle->bicycle), so there is a single BaseUrl (not one per profile). Any/Air is never routed. NFR7: self-hosted, so coordinates never leave the deployment (the region .pbf is fetched only at tile-build time, never per route).",
"Valhalla": {
  "//BaseUrl": "Base URL of the self-hosted Valhalla routing engine. Default http://valhalla:8002 (the compose service name + port).",
  "BaseUrl": "http://valhalla:8002",
  "//RequestTimeoutSeconds": "Per-request Valhalla /route timeout (seconds); a timeout degrades the leg to Estimated. Default 10.",
  "RequestTimeoutSeconds": 10,
  "//GeometryPrecision": "Encoded-polyline precision (6 = 'polyline6'). MUST match the leafletInterop.js decoder (factor 1e-6). Default 6.",
  "GeometryPrecision": 6
},
```

Also update the existing `//Provider` comment so it mentions Valhalla as the measured option (keep the wording aligned with the project's voice; e.g. note that `'Valhalla'` selects the self-hosted measured provider and missing/unrecognized → Mock). **Leave the `Provider` value itself as `"Mock"`** — Valhalla is opt-in; do not change the default. **Do not delete the `Osrm` block** (Epic 3 owns that).

### What must NOT change

- **`AddTripServices()` parameterless overload** — byte-for-byte unchanged (AC 2, NFR-13). The integration host composes via this overload (Mock), so the Trip integration filter must keep passing.
- **`ValhallaTravelTimeProvider` / `ValhallaOptions` / `ValhallaRouteUnavailableException`** — done in Story 2.2; do not edit. Just reference `ValhallaTravelTimeProvider.HttpClientName` and `ValhallaOptions`.
- **`UiStrings.TripRoutingAttributionValhalla`** — done in Story 2.1; `ValhallaTravelTimeProvider.Attribution` already returns it. Do not touch `UiStrings`.
- **The attribution chain** — `TripViewModel.RoutingAttributionHtml => travelTimeProvider?.Attribution;` (line ~39 of `Components/Shared/Trip/TripViewModel.cs`) → MapPage → LeafletMap is **already wired and provider-agnostic**. Selecting Valhalla makes `provider.Attribution` non-null, so the chain surfaces ODbL automatically. No chain edit is needed; AC 3 is satisfied by the DI swap alone. Verify by reading `RoutingAttributionHtml` (you do not need to change it).
- **OSRM branch / OSRM config block** — left present-but-dead. `TravelTime:Provider=Osrm` ceases to be recognized after this story (it will fall to the `else`/Mock arm), which is the intended behavior; Story 3.1 later adds the prominent warn-and-fallback for retired ids and Story 3.3 deletes the OSRM artifacts. **This story does not add the warn-and-fallback** (that is Story 3.1) — an unknown/retired id simply falls to Mock here, as the existing `else` already does.

### Verified existing contracts (read before coding)

- **`ValhallaTravelTimeProvider.HttpClientName == "valhalla"`** (`Services/Trip/ValhallaTravelTimeProvider.cs`, line ~26) — the named-client constant. The provider resolves its client via `httpClientFactory.CreateClient(HttpClientName)`.
- **`ValhallaTravelTimeProvider` ctor** — primary constructor `(IHttpClientFactory, IOptions<ValhallaOptions>, IOptions<TravelTimeOptions>, ILogger<ValhallaTravelTimeProvider>)`. All four are satisfied by the registrations in this branch (`AddHttpClient`, `Configure<ValhallaOptions>`, the trailing `Configure<TravelTimeOptions>`, and the host's logging). `ProducesMeasuredFidelity => true`, `Source => TravelTimeSource.Valhalla`, `Attribution => UiStrings.TripRoutingAttributionValhalla`.
- **`ValhallaOptions`** (`Services/Trip/ValhallaOptions.cs`) — `BaseUrl="http://valhalla:8002"`, `RequestTimeoutSeconds=10`, `GeometryPrecision=6`; binds from `TravelTime:Valhalla`. `CostingFor(mode)` maps Drive→auto/Walk→pedestrian/Cycle→bicycle.
- **`MockTravelTimeProvider.Attribution == null`** — the default rung declares no routing attribution (AC 4). Confirm while reading.
- **`TripViewModel.RoutingAttributionHtml`** (`Components/Shared/Trip/TripViewModel.cs` line ~39) — `=> travelTimeProvider?.Attribution;`. Provider-agnostic, unchanged.
- **OSRM branch shape** (`TripServicesExtensions.cs` lines ~46–60) — the exact pattern (Configure options → read timeout → AddHttpClient with Timeout → AddSingleton provider) this story mirrors for Valhalla.

## Constraints (NFRs)

- **AD-4 — DI selection.** Replace the `=="Osrm"` branch with a `=="Valhalla"` branch in `AddTripServices(IConfiguration)`; bind `TravelTime:Valhalla`, register the named `"valhalla"` client (with timeout from `RequestTimeoutSeconds`), register `ValhallaTravelTimeProvider` as active. Parameterless overload keeps Mock. Run the Trip integration filter after.
- **FR-10 / NFR8 — Attribution.** When Valhalla is active, its OSM/ODbL routing attribution surfaces via the existing chain; when the default is active, no routing attribution shows. This story satisfies both by selecting the provider whose `Attribution` is (Valhalla) non-null / (Mock) null — the chain is unchanged.
- **NFR-13 — DI seam integrity.** The parameterless overload registers the smart-haversine default the integration host composes by hand; the `IConfiguration` overload adds the config-selected Valhalla provider. The Trip integration filter MUST pass after the DI change (recurring integration-host regression point).
- **NFR-12 — Build discipline.** Clean under `TreatWarningsAsErrors` + analyzers; no group-B violations; new/changed config comments are JSON (no analyzer impact). The branch swap is type-for-type with the existing OSRM branch, so no new analyzer surface.
- **NFR7 — Privacy.** Valhalla is self-hosted; the client targets only the configured internal `BaseUrl`. No egress guard needed (the automated no-egress test is Story 2.6). This story adds no outbound surface beyond the internal base URL.
- **Additive / no regression.** Only the config-selected branch and the `appsettings.json` doc change. Parameterless overload, provider classes, attribution chain, and OSRM artifacts are untouched. No new types, no schema change, no EF migration.

## Testing

This story is DI wiring. Prefer **integration-style** coverage that resolves the real container, plus a focused unit test for the branch selection if the existing test surface supports it. **Do not add or modify production/test code beyond what proves the ACs** — and do not invent a parallel DI helper.

Required coverage (AC 6 is the hard gate):

- **Parameterless overload still registers Mock (AC 2, NFR-13).** Build a `ServiceCollection`, call `AddTripServices()` (no config), resolve `ITravelTimeProvider`, assert it is `MockTravelTimeProvider` and `Attribution` is `null`. (If a test like this already exists for the parameterless overload, confirm it stays green rather than duplicating it.)
- **With `Provider=Valhalla`, the active provider is Valhalla and attribution surfaces (AC 3).** Build an `IConfiguration` (in-memory) with `TravelTime:Provider=Valhalla` (and a `TravelTime:Valhalla:BaseUrl` so options bind), call `AddTripServices(configuration)`, resolve `ITravelTimeProvider`, assert it is `ValhallaTravelTimeProvider`, `ProducesMeasuredFidelity` is `true`, and `Attribution == UiStrings.TripRoutingAttributionValhalla` (non-null). Resolving the provider requires `IHttpClientFactory` + logging — add `services.AddHttpClient()` / `services.AddLogging()` to the test collection if the container needs them (mirror how existing DI tests for this extension set up the collection). Optionally assert `RoutingAttributionHtml` surfaces it by constructing/inspecting the VM if an existing VM test pattern makes that cheap; otherwise asserting `provider.Attribution` is sufficient since the chain is the unchanged `=> provider.Attribution`.
- **Default active shows no routing attribution (AC 4).** With no `TravelTime:Provider` (or a non-Valhalla value), resolve `ITravelTimeProvider` from `AddTripServices(configuration)` and assert it is `MockTravelTimeProvider` with `Attribution == null` (⇒ `RoutingAttributionHtml` would be null). Cover at least one non-Valhalla value (e.g. `"Osrm"` or `"Bogus"`) to prove the retired/unknown id falls to Mock (the warn-and-fallback messaging is Story 3.1, not asserted here).
- **Trip integration filter passes (AC 6, NFR-13).** Run `dotnet test --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"` and confirm green. This is the recurring regression point after any DI change.

Look first at the existing test surface for `TripServicesExtensions` / DI composition (search the test project for `AddTripServices`) and mirror its setup (in-memory `ConfigurationBuilder`, `ServiceCollection`, `BuildServiceProvider`). Keep the fast suite green.

## Build/Test commands

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Fast tests: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Trip integration: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`

## Dev Notes

- **The whole story is a one-branch swap + one config block.** Replace the `=="Osrm"` arm with a `=="Valhalla"` arm (Configure `ValhallaOptions` → read `RequestTimeoutSeconds` → `AddHttpClient(ValhallaTravelTimeProvider.HttpClientName, …)` with `client.Timeout` → `AddSingleton<ITravelTimeProvider, ValhallaTravelTimeProvider>()`). Add the documented `TravelTime:Valhalla` block to `appsettings.json`. Nothing else.
- **This closes Story 2.2's deferred timeout wiring.** Story 2.2 added `ValhallaOptions.RequestTimeoutSeconds` but explicitly deferred wiring it into the named client. Set `client.Timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds))` here, reading `TravelTime:Valhalla:RequestTimeoutSeconds` (default 10), exactly mirroring the OSRM branch. Note this in the Dev Agent Record.
- **No `User-Agent` for Valhalla.** OSRM's UA header is a public-demo-server requirement; self-hosted Valhalla on the internal network does not need it. Omit it (or add a harmless `(+valhalla-routing)` UA if you want symmetry — not required).
- **Last-registration-wins.** `AddTripServices()` registers Mock first; the branch re-registers the active provider after, so it wins single-service resolution. Keep this ordering — it is the existing pattern and what AC 2 depends on.
- **Attribution is already wired — do not touch the chain.** `RoutingAttributionHtml => provider.Attribution` is provider-agnostic. Selecting Valhalla flips `Attribution` from null (Mock) to the ODbL string automatically. AC 3/AC 4 are satisfied by the DI swap; no MapPage/LeafletMap/VM edit.
- **Do not add warn-and-fallback.** A retired/unknown `TravelTime:Provider` (e.g. `Osrm`) now silently falls to the `else`/Mock arm. The prominent startup warning for retired ids is **Story 3.1** (FR-15, AD-7) — out of scope here. The `else` already does the fallback behavior this story needs.
- **Do not remove OSRM.** The OSRM branch, `OsrmOptions`, provider, exception, named `"osrm"` client, and the `TravelTime:Osrm` config block all stay present-but-dead. Epic 3 (Story 3.3 / FR-14) deletes them. Touching them here breaks the controlled removal sequence.
- **Run the Trip integration filter** after the DI change (AD-4 explicitly calls for it; NFR-13 recurring regression point). The integration host composes the **parameterless** overload (Mock), so the filter proves the unchanged overload still works and the container still builds.

### References

- [Source: _bmad-output/planning-artifacts/epics.md#Story 2.4] — acceptance criteria
- [Source: _bmad-output/planning-artifacts/epics.md#Additional Requirements] — AD-4 (replace `=="Osrm"` branch with `=="Valhalla"`; parameterless keeps Mock; run Trip integration filter), AD-3 (`ValhallaOptions` defaults + named `"valhalla"` client), AD-9 (Valhalla ODbL attribution via provider.Attribution → VM → MapPage → LeafletMap)
- [Source: _bmad-output/planning-artifacts/architecture.md] — AD-4 DI selection; the two-overload DI seam (NFR-13)
- [Source: LucidCartographer/Configuration/TripServicesExtensions.cs] — both `AddTripServices` overloads; the `=="Osrm"` branch (lines ~46–60) this story mirrors for Valhalla; the parameterless overload (unchanged)
- [Source: LucidCartographer/Services/Trip/ValhallaTravelTimeProvider.cs] — `HttpClientName="valhalla"`, ctor deps, `Attribution`/`Source`/`ProducesMeasuredFidelity` (Story 2.2)
- [Source: LucidCartographer/Services/Trip/ValhallaOptions.cs] — `BaseUrl`/`RequestTimeoutSeconds`/`GeometryPrecision` defaults; binds from `TravelTime:Valhalla` (Story 2.2)
- [Source: LucidCartographer/Services/UiStrings.cs] — `TripRoutingAttributionValhalla` (Story 2.1)
- [Source: LucidCartographer/Components/Shared/Trip/TripViewModel.cs] — `RoutingAttributionHtml => travelTimeProvider?.Attribution;` (line ~39, unchanged chain)
- [Source: LucidCartographer/appsettings.json] — existing `TravelTime` section + `//`-documented `Osrm` block (the style the new `Valhalla` block mirrors)
- [Source: _bmad-output/implementation-artifacts/stories/story-2-2-valhallatraveltimeprovider-measured-all-ground-modes.md] — Story 2.2 added the provider/options/named-client and noted `RequestTimeoutSeconds` wiring deferred to DI (this story)
- [Source: _bmad-output/implementation-artifacts/stories/story-2-3-capability-gated-recompute-trigger-and-degrade.md] — format template; Story 2.3 (done) consumes the seam this DI step activates

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (BMAD dev-story)

### Debug Log References

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug` → 0 Warning(s), 0 Error(s) (clean under TreatWarningsAsErrors).
- Fast suite: `--filter "FullyQualifiedName!~Integration"` → Passed 1027, Failed 0, Skipped 0.
- Trip integration filter: `--filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"` → Passed 20, Failed 0, Skipped 0 (the recurring NFR-13 regression gate — GREEN).

### Completion Notes List

- **AC 1 — DI branch swap.** Replaced the `=="Osrm"` branch in `AddTripServices(IConfiguration)` with a `=="Valhalla"` branch that (a) binds `ValhallaOptions` from `configuration.GetSection("TravelTime:Valhalla")`, (b) registers the named `ValhallaTravelTimeProvider.HttpClientName` (`"valhalla"`) client with `client.Timeout = TimeSpan.FromSeconds(Math.Max(1, RequestTimeoutSeconds ?? 10))`, and (c) registers `ValhallaTravelTimeProvider` as the active `ITravelTimeProvider` singleton. The `else` arm still registers `MockTravelTimeProvider`. No `User-Agent` header (self-hosted, internal network — not needed). Trailing `Configure<TravelTimeOptions>` + `AddHostedService` unchanged.
- **AC 1b / closes Story 2.2 deferred wiring.** This branch wires `ValhallaOptions.RequestTimeoutSeconds` into the named client's `client.Timeout`, which Story 2.2 explicitly deferred to the DI step. The Valhalla provider resolves its client via `httpClientFactory.CreateClient(HttpClientName)`, so this is the per-request timeout it relies on.
- **AC 2 — parameterless overload.** `AddTripServices()` is byte-for-byte unchanged; it still registers `MockTravelTimeProvider` first, and the `IConfiguration` overload re-registers the config-selected provider after (last-registration-wins). New unit test `ParameterlessOverload_RegistersMock_WithNoAttribution` asserts it (NFR-13).
- **AC 3 — Valhalla attribution.** With `TravelTime:Provider=Valhalla`, the active provider is `ValhallaTravelTimeProvider` with `ProducesMeasuredFidelity == true` and `Attribution == UiStrings.TripRoutingAttributionValhalla` (non-null). The existing unchanged chain `provider.Attribution → TripViewModel.RoutingAttributionHtml (=> travelTimeProvider?.Attribution) → MapPage → LeafletMap` surfaces it; no chain edit needed. Verified `RoutingAttributionHtml` is the provider-agnostic pass-through.
- **AC 4 — default no attribution.** With missing / empty / `"Mock"` / non-Valhalla values (including the retired `"Osrm"` id), the provider resolves to `MockTravelTimeProvider` whose `Attribution == null` ⇒ `RoutingAttributionHtml` would be null ⇒ no routing attribution control content. Covered by Theory cases.
- **AC 5 — appsettings.json.** Added a `TravelTime:Valhalla` section with documented `//`-prefixed defaults (`BaseUrl=http://valhalla:8002`, `RequestTimeoutSeconds=10`, `GeometryPrecision=6`), placed adjacent to the `Osrm` block. Updated the `//Provider` comment to mention Valhalla as the measured option and that missing/unrecognized (including retired `Osrm`) falls to Mock. The `Provider` value stays `"Mock"` (opt-in). OSRM config block left in place (Epic 3 / FR-14 owns its removal).
- **AC 6 / AC 7 — gates.** Trip integration filter GREEN (20/20); fast suite GREEN (1027/1027); build clean under TreatWarningsAsErrors with no group-B analyzer violations.
- **Test surface.** Updated the existing `TripProviderConfigSelectionTests` (formerly the OSRM-selection test). The OSRM-resolves test became the Valhalla-resolves-and-surfaces-attribution test (since `Osrm` is no longer recognized and now falls to Mock — intended; warn-and-fallback is Story 3.1). Added `AddHttpClient()` to the test collection so the Valhalla provider ctor's `IHttpClientFactory` resolves. Added an `"Osrm"` Theory case proving the retired id falls to Mock, plus the parameterless-overload Mock test.
- **Not changed (per scope):** OSRM branch / OSRM config block (present-but-dead, Epic 3 owns deletion), provider/options/exception classes (Story 2.2), `UiStrings` (Story 2.1), the attribution chain. No warn-and-fallback added (Story 3.1). No new types, no schema change.

### File List

- `LucidCartographer/Configuration/TripServicesExtensions.cs` (modified) — replaced the `=="Osrm"` branch with the `=="Valhalla"` branch (Configure ValhallaOptions → wire RequestTimeoutSeconds into named `"valhalla"` client → register ValhallaTravelTimeProvider as active).
- `LucidCartographer/appsettings.json` (modified) — added documented `TravelTime:Valhalla` section; updated `//Provider` comment.
- `LucidCartographer.Tests/Services/TripProviderConfigSelectionTests.cs` (modified) — repointed OSRM-selection tests to Valhalla; added parameterless-overload Mock test, attribution assertions, and `"Osrm"`/empty retired-id Theory cases.

### Change Log

| Date       | Change |
|------------|--------|
| 2026-06-24 | Story drafted (create-story): config/DI selection of the Valhalla provider. Status → ready-for-dev. |
| 2026-06-24 | Dev-story: flipped DI branch to Valhalla (binds ValhallaOptions, wires RequestTimeoutSeconds into named "valhalla" client, registers ValhallaTravelTimeProvider active); added documented TravelTime:Valhalla appsettings block; updated DI selection tests. Build clean (0/0); fast 1027/1027; Trip integration 20/20. Status → review. |
| 2026-06-24 | Senior Developer Review (AI): APPROVE. All 7 ACs verified implemented against the Story 2.4 surface (DI branch, appsettings, tests). 0 Critical / 0 High / 0 Medium / 1 Low (stale OSRM example in parameterless-overload comment — auto-fixed to Valhalla). Math.Max(1,…) timeout clamp reviewed and judged sound. Build clean (0/0); fast 1033/1033; Trip integration 20/20. Status → done. |

## Senior Developer Review (AI)

**Reviewer:** satec\yurik (autonomous story-automator review)
**Date:** 2026-06-24
**Outcome:** APPROVE — Status → done

### Scope

Reviewed Story 2.4's changes ONLY (config/DI selection of the Valhalla provider). The working tree also carries intermingled uncommitted changes from Epic 1 and Stories 2.1–2.3 (ITravelTimeProvider, TravelTimeSource, MockTravelTimeProvider, ValhallaTravelTimeProvider/Options/Exception, UiStrings, leafletInterop.js, TravelTimeComputationBackgroundService, detour factors). Those are out of scope and were NOT reviewed or flagged. Review surface:

- `LucidCartographer/Configuration/TripServicesExtensions.cs` — the `=="Valhalla"` DI branch
- `LucidCartographer/appsettings.json` — the `TravelTime:Valhalla` block + `//Provider` comment
- `LucidCartographer.Tests/Services/TripProviderConfigSelectionTests.cs`

### Acceptance Criteria — all verified IMPLEMENTED

- **AC 1 (DI branch swap).** `TripServicesExtensions.cs:46` — `string.Equals(providerId, "Valhalla", StringComparison.OrdinalIgnoreCase)` branch: (a) binds `ValhallaOptions` from `TravelTime:Valhalla` (:50); (b) registers the named `ValhallaTravelTimeProvider.HttpClientName` ("valhalla") client with `client.Timeout` from `RequestTimeoutSeconds` (:52–56) — closing Story 2.2's deferred timeout wiring; (c) registers `ValhallaTravelTimeProvider` as active `ITravelTimeProvider` singleton (:58). `else` arm keeps Mock (:62). No `User-Agent` (correct — self-hosted). Constant referenced, not hardcoded.
- **AC 2 (parameterless overload unchanged).** `AddTripServices()` still registers Mock; `IConfiguration` overload calls it first (:41) then re-registers the active provider after (last-registration-wins). Verified by `ParameterlessOverload_RegistersMock_WithNoAttribution`.
- **AC 3 (Valhalla attribution).** Confirmed `ValhallaTravelTimeProvider.Attribution => UiStrings.TripRoutingAttributionValhalla`, `ProducesMeasuredFidelity => true`, `Source => TravelTimeSource.Valhalla`; the chain `provider.Attribution → TripViewModel.RoutingAttributionHtml (=> travelTimeProvider?.Attribution, line 39) → MapPage → LeafletMap` is unchanged and provider-agnostic. DI swap alone surfaces ODbL.
- **AC 4 (default no attribution).** `MockTravelTimeProvider.Attribution => null`; `else` arm covers missing / empty / `Mock` / retired `Osrm` / unknown. Verified by the missing-key and `[Mock|""|Osrm|something-else]` Theory cases.
- **AC 5 (appsettings).** `appsettings.json:70–78` adds `//Valhalla` + `Valhalla` block with documented defaults (`BaseUrl=http://valhalla:8002`, `RequestTimeoutSeconds=10`, `GeometryPrecision=6`) mirroring the `Osrm` comment-key style; `//Provider` (:55) updated to mention Valhalla and that retired `Osrm` falls to Mock; `Provider` value left `"Mock"` (opt-in); OSRM block left in place (Epic 3 owns removal).
- **AC 6 (Trip integration filter).** GREEN — 20/20.
- **AC 7 (build + suites under TreatWarningsAsErrors).** Build 0/0; fast suite GREEN.

### Timeout clamp (Math.Max(1, timeoutSeconds)) — reviewed, sound

`c.Timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds))` (:55). A non-positive `HttpClient.Timeout` throws `ArgumentOutOfRangeException` at construction, so the clamp prevents a hard startup crash. Flooring a misconfigured `0`/negative to 1s is a defensible fail-operational choice for an opt-in self-hosted provider and mirrors the OSRM branch exactly. The "surface invalid config / warn" concern belongs to Story 3.1 (FR-15, AD-7), not here. Covered by `Provider_Valhalla_NamedHttpClient_ClampsNonPositiveTimeout`. Not masking a case this story owns — no finding.

### Test quality

`TripProviderConfigSelectionTests` uses real assertions across every AC: parameterless Mock + null attribution, Valhalla resolves with measured fidelity + ODbL attribution, case-insensitivity (valhalla/VALHALLA/VaLhAlLa), options binding (custom BaseUrl/timeout/precision), named-client timeout wiring (37s) and clamp (0→1s), and missing/Mock/empty/Osrm/unknown → Mock. No placeholders.

### Findings

| Severity | Count | Notes |
|----------|-------|-------|
| Critical | 0 | — |
| High | 0 | — |
| Medium | 0 | — |
| Low | 1 | `TripServicesExtensions.cs:30` parameterless-overload comment said "config-selected provider (e.g., OSRM) wins"; OSRM is now the retired/dead id. **Auto-fixed** to "(e.g., Valhalla)". Cosmetic, no behavioral/test impact. |

### Auto-fixes applied

1. `TripServicesExtensions.cs:30` — comment example `OSRM` → `Valhalla`. Rebuilt clean (0/0).

### Verification

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug` → 0 Warning(s), 0 Error(s).
- Fast suite: `--filter "FullyQualifiedName!~Integration"` → Passed 1033, Failed 0, Skipped 0.
- Trip integration: `--filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"` → Passed 20, Failed 0, Skipped 0.

**Decision:** 0 Critical issues → Status set to `done`; sprint-status synced.
