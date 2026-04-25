# LucidCartographer

A self-hosted Blazor Server app for managing geographic points-of-interest: multi-source import (GPX, KML, GeoJSON, CSV, Google Maps lists), automatic enrichment, set operations across collections (union / intersect / subtract / dedup), and KML/GPX export.

## Architecture

```
+----------------+      +-----------------+      +-------------+      +---------+
|  Components/   |  ->  |  ViewModels     |  ->  |  Services/  |  ->  |  Data/  |
|  (.razor)      |      |  (*ViewModel.cs)|      |  (vertical  |      |  (EF    |
|  markup +      |      |  state +        |      |   slices)   |      |   Core) |
|  bindings only |      |  orchestration  |      |             |      |         |
+----------------+      +-----------------+      +-------------+      +---------+
```

- **Components** are thin view hosts: markup, bindings, a 12-line lifecycle bridge to the ViewModel.
- **ViewModels** (one per heavy page) hold all UI state and orchestrate services. Registered as `Transient`. Notify the view via `event Action? StateChanged`.
- **Services** are vertical slices by domain: `Import/`, `Enrichment/`, `Operations/`, `Auth/`, `Export/`. Interface-first.
- **Data** is EF Core with SQLite. `IDbContextFactory<AppDbContext>` for thread-safe contexts.

`Program.cs` is a composition root only — DI registrations live in `Configuration/*Extensions.cs`, minimal-API endpoints in `Endpoints/*Endpoints.cs`, and one-shot startup tasks in `Services/StartupCleanupService.cs`.

## Run

```bash
# Local
dotnet run --project LucidCartographer

# Docker
docker-compose up
```

The DB path is resolved from `DB_PATH` env var, then `Database:Path` config, then defaults to `data/cartographer.db` under `ContentRootPath`.

Auth is mandatory in non-Development environments — set `Auth:Password` or `Auth:PasswordHash` (see `appsettings.json`). The app refuses to start with the literal value `changeme`.

## Test

```bash
dotnet test
```

Three layers:
- **Unit tests** — pure logic (importers, exporters, orchestrators, ViewModels).
- **Component tests** — bUnit for binding/render coverage.
- **Integration tests** — full Blazor circuit + SQLite via `IntegrationTestBase`.

## Design decision codes

Comments in code reference these codes — search the codebase for context:

- `ARCH-CRIT-*` — critical architectural decisions (DB migration, password enforcement, CSP).
- `ARCH-HIGH-*` — high-priority refactors (DI lifetime corrections, defense-in-depth, header ordering).
- `ARCH-LOW-*` — low-priority hygiene (unobserved task logging, etc.).
- `HIGH-*` — concurrency and resource-control decisions (scraper single-flight, etc.).
- `MED-*` — medium-priority infrastructure (response compression, OS-independent paths).
- `IE-*` — import/enrichment pipeline notes.

## Folder map

```
LucidCartographer/
  Components/
    Layout/               MainLayout, LoginLayout
    Pages/                <Page>.razor + <Page>ViewModel.cs (markup + state)
    Shared/               reusable components (LeafletMap, PoiTable, ...)
  Configuration/          IServiceCollection extension methods
  Endpoints/              MapXxxEndpoints extension methods
  Services/
    Auth/                 SessionStore, PasswordHasher
    Import/               IFileImporter implementations + orchestrator + Coravel queue + scraper
    Enrichment/           PoiEnrichmentBackgroundService + helpers
    Operations/           PoiMatcher, SetOperationService
    Export/               IFileExporter implementations
    StartupCleanupService.cs    one-shot startup tasks
    PoiService.cs, LeafletMapService.cs, helpers
  Data/
    Entities/             POCO entities
    AppDbContext.cs       Fluent API + check constraints + indexes
  Migrations/             EF Core migrations
  Program.cs              composition root only
  wwwroot/                CSS, JS, Leaflet
LucidCartographer.Tests/
  ViewModels/             plain xUnit tests of *ViewModel.cs
  Components/             bUnit component tests
  Integration/            full-circuit tests with real DbContext
```
