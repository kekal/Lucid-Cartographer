# Source Tree Analysis

_Annotated folder map. Monolith: one app project + one test project._

```
maps_editor/
├── Directory.Build.props          # Shared build config: LangVersion 14, Nullable, warnings-as-errors, analyzers
├── LucidCartographer.slnx         # Solution
├── README.md
│
├── LucidCartographer/             # ── The application ──
│   ├── Program.cs                 # Composition root ONLY (DI + middleware wiring)
│   │
│   ├── Components/                # Blazor UI (thin view hosts)
│   │   ├── Layout/                #   MainLayout, LoginLayout
│   │   ├── Pages/                 #   <Page>.razor + <Page>ViewModel.cs (markup + state)
│   │   ├── Shared/                #   Reusable: LeafletMap, PoiTable, CollectionSidebar,
│   │   │   │                      #   PoiDetailPane, Mobile* screens, dialogs, ViewportObserver
│   │   │   └── Trip/              #   Trip View UI + TripViewModel (toggle, wide stop list/takeover, LegConnector,
│   │   │                          #   LegModePill per-leg mode, badges; legacy TravelModeSelector now mobile-only)
│   │   ├── App.razor, Routes.razor, _Imports.razor
│   │
│   ├── Configuration/             # IServiceCollection / IApplicationBuilder extensions (DI lives here)
│   │   ├── *ServicesExtensions.cs, *PipelineExtensions.cs   # database, POI, import, enrichment, dedup,
│   │   │                          #   browser, export, view models, resilience
│   │   ├── AppAuthenticationExtensions.cs, AuthRouteGuardExtensions.cs,
│   │   ├── OAuthFrontdoorExtensions.cs, McpServerExtensions.cs, SecurityHeadersExtensions.cs
│   │
│   ├── Endpoints/                 # Minimal-API endpoint mappers
│   │   ├── AuthEndpoints.cs       #   /auth/login, /logout
│   │   ├── OAuthEndpoints.cs      #   /connect/authorize|token|register
│   │   ├── PoiImageEndpoints.cs   #   /api/poi-image/{id} (ETag/304)
│   │   ├── NoVncProxyEndpoint.cs  #   /google-session/novnc/** (HTTP+WS proxy)
│   │   └── McpApiKeyFilter.cs     #   /mcp auth filter
│   │
│   ├── Services/                  # Business logic — vertical slices, interface-first
│   │   ├── Auth/                  #   PasswordHasher (PBKDF2 600k), SessionStore
│   │   ├── Browser/               #   BrowserSessionManager, GoogleBrowserLock, GoogleSignIn/Consent
│   │   ├── Import/                #   IFileImporter (GPX/KML/GeoJSON/CSV), ImportOrchestrator, Coravel queue
│   │   ├── Enrichment/            #   PoiEnrichmentBackgroundService, PoiDetailEnricher, EnrichmentStateMachine
│   │   ├── Operations/            #   SetOperationService, PoiMatcher, PoiDeduplication(+BackgroundService)
│   │   ├── Export/                #   IFileExporter (GPX/KML), GoogleMapsListExporter, ExportBackgroundService
│   │   ├── Mcp/                   #   PoiReadTools, PoiWriteTools, EnrichmentTools, TripTools, prompts/resources
│   │   ├── Trip/                  #   Trip Planning: ITravelTimeProvider (Mock/OSRM), RouteSegment cache +
│   │   │                          #   invalidation, TravelTimeComputationBackgroundService, TripOrderingService,
│   │   │                          #   TspSolver, DistanceMatrixService, ItineraryTimeline (see trip-planning.md)
│   │   ├── StartupCleanupService.cs   # ENTRY: one-shot startup (migrate, seed admin, vacuum)
│   │   ├── PoiService.cs, LeafletMapService.cs, ViewportService.cs, UiStrings.cs
│   │   └── SqliteWriteLock.cs, GeoUtils.cs, PoiUrlHelper.cs, ...   # shared helpers
│   │
│   ├── Data/                      # Persistence
│   │   ├── AppDbContext.cs        #   Fluent API + check constraints + indexes + .UseOpenIddict()
│   │   └── Entities/              #   Poi, PoiImage, PoiCollection, PoiCollectionItem, Tag, PoiTag, Session, User,
│   │   │                          #   RouteSegment (trip leg cache), TravelMode + Fidelity (string enums)
│   │
│   ├── Migrations/                # EF Core migrations (applied at startup)
│   └── wwwroot/                   # css/ (Tailwind input + compiled), js/ (leafletInterop, viewport, theme, ...)
│
└── LucidCartographer.Tests/       # ── Tests ──
    ├── ViewModels/                #   plain xUnit tests of *ViewModel.cs
    ├── Components/                #   bUnit component tests
    ├── Integration/               #   IntegrationTestBase + Mobile*TestBase: real circuit + Playwright + temp SQLite
    ├── Services/                  #   service-level unit tests
    └── TestData/                  #   sample import files (copied to output)
```

## Entry Points

- **`Program.cs`** — process bootstrap (builder → register → pipeline → `MapRazorComponents<App>()`).
- **`Services/StartupCleanupService.cs`** (`IHostedService`) — runs once on boot: applies migrations, seeds the admin user, sweeps temp files, revives stuck imported POIs, vacuums sessions.
- **`Components/App.razor` / `Routes.razor`** — Blazor root + routing.
- Background services started at boot: enrichment, deduplication, import (Coravel), export.

## Critical Directories

| Directory | Why it matters |
|-----------|----------------|
| `Configuration/` | All DI wiring — add new service registrations here, not in `Program.cs`. |
| `Endpoints/` | All HTTP routes — add new minimal-API endpoints here. |
| `Services/<Slice>/` | Add business logic to the matching vertical slice, interface-first. |
| `Data/` + `Migrations/` | Schema; any entity change needs a new migration. |
| `wwwroot/js/` | JS interop modules referenced by components. |
