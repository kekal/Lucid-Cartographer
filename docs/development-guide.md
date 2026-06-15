# Development Guide

## Prerequisites

- **.NET 10 SDK** — required to build, even though the app targets `net8.0`. `LangVersion` is pinned to `14.0` in `Directory.Build.props`; a mismatched SDK fails loudly (CS9202). Keep the pin, the app `.csproj`, and the Dockerfile build-stage SDK in sync.
- **No Node.js needed** — the Tailwind CSS standalone CLI (v3.4.17) is auto-downloaded into `obj/` by an MSBuild target on first build.
- **Playwright/Chromium** — enrichment, scraping, and Google-list export drive a real browser. `dotnet build` restores `Microsoft.Playwright`; browsers are present in the Docker runtime image. For local browser work you may need `playwright install chromium` (via the generated `playwright.ps1`/CLI).

## Build & Run

```bash
# Restore + build
dotnet build

# Run locally (compiles Tailwind, serves the app)
dotnet run --project LucidCartographer

# Docker
docker-compose up
```

DB path resolves from `DB_PATH` → `Database:Path` → `data/cartographer.db` (under ContentRootPath).

### First-run admin
On first boot with an empty `Users` table, `StartupCleanupService` seeds an `admin` user with a random 24-char password printed once to the log under `INITIAL ADMIN USER CREATED` (or set `Auth:InitialAdminPassword` / `Auth__InitialAdminPassword`). Capture it from the logs — it is not shown again.

### Local auth convenience
In Development, `Auth:BypassLocalAddresses` is effectively on for RFC1918/loopback, so localhost skips the login form. `Mcp:AllowLocalNetworkBypass` similarly lets local `/mcp` calls through without an API key.

## Database / Migrations

Schema changes (entity or Fluent-config edits) require a new migration:

```bash
dotnet ef migrations add <Name> --project LucidCartographer
```

Migrations are applied automatically at startup via `MigrateAsync` (ARCH-CRIT-01) — do **not** rely on `EnsureCreated`, and never hand-edit an applied migration (SQLite has limited `ALTER` support).

## Testing

```bash
dotnet test
```

Three layers (`LucidCartographer.Tests/`):

- **Unit** — pure logic: importers, exporters, `ImportOrchestrator`, `SetOperationService`, `PoiMatcher`, `EnrichmentStateMachine`, ViewModels, `PoiService`, and the Trip slice (`TspSolver`, `ItineraryTimeline`, `TripOrderingService`, `DistanceMatrixService`, travel-time providers). After any Trip DI/VM-ctor change, run the Trip integration test filter (the parameterless `AddTripServices` overload is what the integration host composes by hand).
- **Component** — bUnit render/binding tests under `Components/`.
- **Integration** — `IntegrationTestBase` spins up a real `WebApplication` + Playwright + a fresh temp SQLite DB per test, pointing `WebRootPath` at the app's `wwwroot`. Desktop and mobile have dedicated bases (`MobileTestBase`, `Mobile*Tests`); cover both when changing responsive UI.

`InternalsVisibleTo("LucidCartographer.Tests")` is set, so tests can reach internals directly.

## Code Conventions (enforced)

- `TreatWarningsAsErrors=true`, `Nullable=enable`, `ImplicitUsings=enable`, `EnforceCodeStyleInBuild=true` — a warning is a build break.
- Analyzers: Meziantou + Microsoft.VisualStudio.Threading. The `NoWarn` list in `Directory.Build.props` has permanent suppressions (group A) and **baseline-suppressed legacy violations (group B)** — new code must not add any group-B violation.
- Don't add `ConfigureAwait(false)` (Blazor Server needs the circuit sync context — MA0004 suppressed on purpose).
- UI text goes through `Services/UiStrings.cs`. Tailwind class names must be complete static literals (the JIT purges interpolated/fragmented class names).
- Inject `IDbContextFactory<AppDbContext>`, never `AppDbContext` directly.

See [../_bmad-output/project-context.md](../_bmad-output/project-context.md) for the full agent rule set.

## CI/CD

No CI workflows are present (`.github/workflows/` absent) — build/test/deploy are manual. The repo includes `run-lan.cmd` (local LAN run helper) and a Docker build for deployment.
