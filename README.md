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

## Authentication

Per-user accounts persist in the `Users` table. On first run with an empty
table, `StartupCleanupService` bootstraps an `admin` user with a
24-character random password and prints it to the log under a banner
("INITIAL ADMIN USER CREATED"). Capture it from `docker compose logs` or
your hosting platform's log viewer — it is **not shown again**. Password
hashing is PBKDF2-SHA256 (600,000 iterations).

### LAN bypass

`Auth:BypassLocalAddresses` (default **false**) accepts unauthenticated
requests from RFC 1918 / loopback / IPv6 link-local addresses, attaching a
synthetic `lan-bypass` principal. Useful for a single-operator
home/lab deployment that doesn't want a login form on a private network.

> **Warning.** Enable this only when the app server is on a network you
> trust end-to-end. Behind a reverse proxy you **must** also list the
> proxy's IP in `Auth:TrustedProxies` so the framework's
> `ForwardedHeaders` middleware substitutes the original client IP into
> `Connection.RemoteIpAddress`. Without that, every request through the
> proxy looks "local" and bypasses auth — see
> [`docs/auth-rework-proposal.md`](docs/auth-rework-proposal.md).

```json
"Auth": {
  "BypassLocalAddresses": true,
  "TrustedProxies": ["10.0.0.5", "172.20.0.1"]
}
```

## MCP server (Claude integration)

A [Model Context Protocol](https://modelcontextprotocol.io) server is exposed at
**`/mcp`** so AI clients (Claude Code, Claude.ai connectors) can read and manage
POIs/collections without the browser UI. It accepts a request via any of: a
loopback/LAN bypass (Development only), a static `MCP_API_KEY`, or an OAuth access
token from the app's own OAuth 2.1 frontdoor.

To publish it for Claude.ai connectors over a plain HTTPS tunnel, set
`OAuth__Issuer` to the public URL and trust the tunnel in `Auth:TrustedProxies`.
Full walkthrough: [`docs/mcp-oauth-setup.md`](docs/mcp-oauth-setup.md).

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
