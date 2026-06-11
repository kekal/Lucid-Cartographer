---
project_name: 'maps_editor'
user_name: 'Yurik'
date: '2026-06-10'
sections_completed: ['technology_stack', 'language_rules', 'framework_rules', 'testing_rules', 'code_quality_rules', 'workflow_rules', 'critical_rules']
existing_patterns_found: 11
status: 'complete'
rule_count: 18
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
- Services are vertical slices (`Import/`, `Enrichment/`, `Operations/`, `Auth/`, `Export/`), **interface-first**.

### UI Conventions
- **No hardcoded UI text** — all strings go through `UiStrings` (`@UiStrings.*`).
- Large lists use `<Virtualize>` with `@key`. Status regions use `aria-live`; buttons/links carry `aria-label`. Styling is Tailwind utility classes with the project's `surface-*` / `on-surface-*` / `primary` token palette.
- Desktop and mobile are distinct render paths (`Viewport.IsMobile` → `Mobile*Screen`); update both when changing a page's UI.

### Testing Rules
- Three layers: **Unit** (pure logic — importers, exporters, orchestrators, ViewModels), **Component** (bUnit), **Integration** (`IntegrationTestBase`: real `WebApplication` + Playwright + a temp SQLite db per test, points `WebRootPath` at the app's `wwwroot`).
- `InternalsVisibleTo("LucidCartographer.Tests")` is set — test internals directly rather than widening visibility.
- Mobile vs desktop paths have dedicated bases/tests (`MobileTestBase`, `Mobile*Tests`) — cover both when touching responsive UI.

### Conventions Agents Miss
- **DB path resolution order:** `DB_PATH` env var → `Database:Path` config → `data/cartographer.db` under `ContentRootPath`.
- **Design-decision comment codes** in source — search the codebase before changing flagged code: `ARCH-CRIT-*`, `ARCH-HIGH-*`, `ARCH-LOW-*`, `HIGH-*`, `MED-*`, `IE-*`.
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

Last Updated: 2026-06-10
