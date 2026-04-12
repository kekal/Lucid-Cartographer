# Architecture Review: LucidCartographer

**Reviewer:** Principal Architect (Grumpy Division)
**Date:** 2026-04-12
**Scope:** Overall architecture, infrastructure, project structure, dependency injection, Docker, security, performance, and general code hygiene.

---

## CRITICAL

### CRIT-01: Tailwind CSS loaded from CDN play script -- production app ships a development-only tool
**File:** `Components/App.razor`, line 7
**Detail:** `<script src="https://cdn.tailwindcss.com"></script>` is the **Tailwind CDN play script**, explicitly documented by Tailwind as "not for production." It is a 100+ KB JavaScript file that parses your classes at runtime in the browser. This means: (a) Flash of Unstyled Content on every page load; (b) zero cache benefit because styles are generated on the fly; (c) no tree-shaking, no purging, no minification; (d) external CDN dependency that can go offline or change. You need a proper Tailwind build step (`npx tailwindcss -o app.tailwind.css --minify`) integrated into your `dotnet publish`. This is the single most embarrassing thing in the entire project.

### CRIT-02: Playwright bundled in the production web application package
**File:** `LucidCartographer.csproj`, line 17
**Detail:** `Microsoft.Playwright` (v1.49.0) is referenced as a normal runtime dependency. Playwright downloads ~200+ MB of Chromium binaries. This means your production Docker image ships with an entire headless browser engine. Playwright is used only by `GoogleMapsListScraper`, which runs server-side scraping of Google Maps lists. This is a scraping concern that should be extracted to a separate worker/microservice, not embedded in the web app. At the very least, the Dockerfile needs to install Playwright's OS dependencies (`playwright install-deps chromium`), which it does NOT do, meaning the scraper is silently broken in the Docker image.
**Fix — Sidecar container architecture:**
```
┌─────────────────────┐     HTTP API     ┌────────────────────┐
│  LucidCartographer   │ ──────────────> │  Scraper Service    │
│  (Blazor, ~50MB)     │                 │  (Playwright+Chromium│
│  no Playwright dep   │                 │   ~300MB)            │
└─────────────────────┘                 └────────────────────┘
```
1. Create a new .NET minimal API project `LucidCartographer.Scraper` with a single `POST /scrape` endpoint that accepts `{ url: string }` and returns `ScrapeResult` JSON.
2. Move `GoogleMapsListScraper.cs` and the `Microsoft.Playwright` package reference to the new project.
3. In the web app, replace the real scraper implementation with an `HttpGoogleMapsListScraper` that calls the sidecar's HTTP API via `HttpClient`.
4. Update `docker-compose.yml` to run both containers:
```yaml
services:
  cartographer:
    build: ./LucidCartographer
    ports: ["8080:8080"]
    volumes: ["./data:/data"]
    depends_on: [scraper]
  scraper:
    build: ./LucidCartographer.Scraper
    # No exposed ports — only reachable from the internal Docker network
    environment:
      - PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
```
5. The scraper container installs Chromium in its Dockerfile: `RUN playwright install-deps chromium && playwright install chromium`.
6. The web app image drops from ~300MB to ~100MB and no longer needs Chromium OS dependencies.
7. This also mitigates CRIT-03 (authentication) — the scraper is not exposed externally, only the web app is. And it isolates the SSRF risk to an internal-only container.

### CRIT-03: No in-app authentication
**File:** `Program.cs`
**Detail:** Zero authentication middleware. All other NAS services have their own auth. Cloudflare Zero Trust tunnel provides connectivity only, NOT authentication. Anyone with the subdomain URL has full access (confirmed by testing from anonymous tab).
**Fix — Simple password + cookie middleware:**
Single-user personal tool needs minimal auth: a password from an environment variable, verified once, stored in a cookie.
```csharp
// Program.cs — add before app.UseAntiforgery()
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";

    // Allow the login page and static assets
    if (path == "/login" || path.StartsWith("/_framework") || path.StartsWith("/css"))
    {
        await next();
        return;
    }

    // Check auth cookie
    if (context.Request.Cookies["cartographer_auth"] == "authenticated")
    {
        await next();
        return;
    }

    context.Response.Redirect("/login");
});
```
Login page: single password field, compares against `CARTOGRAPHER_PASSWORD` env var, sets a long-lived HttpOnly cookie. No user management, no database, no JWT.
```yaml
# docker-compose.yml
services:
  cartographer:
    environment:
      - CARTOGRAPHER_PASSWORD=your-secret-here
```
Consistent with other NAS services that each handle their own auth.

### CRIT-04: `EnsureCreatedAsync` used instead of migrations
**File:** `Program.cs`, lines 36-41
**Detail:** `db.Database.EnsureCreatedAsync()` will create the schema once and then do nothing. Any schema change (adding a column, index, etc.) will be silently ignored on existing databases. You need proper EF Core migrations. Since `Microsoft.EntityFrameworkCore.Design` is already referenced, there is literally no excuse.

### CRIT-05: Scraper writes debug files to the filesystem unconditionally in production
**File:** `Services/Import/GoogleMapsListScraper.cs`, lines 46-54, 84-89, 172-176
**Detail:** `File.WriteAllText("data/debug_scrape.html", html)` and multiple `page.ScreenshotAsync(new PageScreenshotOptions { Path = "data/debug_scrape.png" })` calls. In production, this writes potentially large HTML dumps and PNG screenshots to the `/data` volume -- the same volume that holds the SQLite database. This is a disk-space leak, a data leakage risk, and debugging code that should never have escaped a feature branch.

---

## HIGH

### HIGH-01: Multiple IFileImporter registrations all resolve to the last one
**File:** `Program.cs`, lines 21-24
**Detail:** Four separate `AddScoped<IFileImporter, ...>` registrations. When you inject `IFileImporter` (singular), DI resolves to the **last** registered implementation (`CsvImporter`). The `ImportOrchestrator` correctly injects `IEnumerable<IFileImporter>`, so the orchestrator works. But if any future service injects `IFileImporter` directly, it will silently get only the CSV importer. This is a DI time bomb. Use a named/keyed pattern or explicitly register `IEnumerable<IFileImporter>` only.

### HIGH-02: Massive code duplication in ImportOrchestrator
**File:** `Services/Import/ImportOrchestrator.cs`
**Detail:** `ImportAsync` (lines 33-140) and `ImportFromScrapedAsync` (lines 142-237) are nearly identical -- the only difference is the source of the parsed POI list and two fields on the collection entity. This is a 100-line copy-paste that doubles the maintenance surface for any import logic changes. Extract the shared "upsert POIs into collection" logic into a private method.

### HIGH-03: N+1 query in `GetVisiblePoisGroupedAsync`
**File:** `Services/PoiService.cs`, lines 31-49
**Detail:** Fetches visible collection IDs, then loops and issues a separate query for each collection. With 20 visible collections, this is 21 database round-trips. Use a single query with `GroupBy` or a `Where(ci => visibleCollectionIds.Contains(ci.PoiCollectionId))` followed by `GroupBy`.

### HIGH-04: N+1 query pattern in ImportOrchestrator per-POI import
**File:** `Services/Import/ImportOrchestrator.cs`, lines 56-127 (and 159-222)
**Detail:** For every single POI, the code does: (1) query by Google Maps URL, (2) query by name, (3) check if linked, (4) `SaveChangesAsync()` per POI. On a 500-POI import, that is 1500-2000 individual database calls. Batch the lookups and use `AddRange` with a single `SaveChangesAsync`.

### HIGH-05: Leaflet and Google Fonts loaded from unpkg/CDNs without SRI
**File:** `Components/App.razor`, lines 10-11
**Detail:** `https://unpkg.com/leaflet@1.9.4/dist/leaflet.js` and multiple Google Fonts URLs are loaded without Subresource Integrity (SRI) hashes. A CDN compromise would inject arbitrary JavaScript into every session. Also creates a hard dependency on external CDN availability — the app breaks if unpkg or Google Fonts are unreachable.
**Fix — Self-host all static assets:**
1. Download and bundle into `wwwroot/lib/`:
```
wwwroot/lib/leaflet/leaflet.js        (from unpkg.com/leaflet@1.9.4)
wwwroot/lib/leaflet/leaflet.css
wwwroot/lib/leaflet/images/            (marker icons)
wwwroot/lib/fonts/manrope.woff2       (from Google Fonts)
wwwroot/lib/fonts/inter.woff2
wwwroot/lib/fonts/material-symbols.woff2
wwwroot/css/tailwind.css               (pre-built with Tailwind CLI, not CDN runtime)
```
2. Replace CDN `<script>`/`<link>` tags in `App.razor` with local paths:
```html
<link rel="stylesheet" href="lib/leaflet/leaflet.css" />
<script src="lib/leaflet/leaflet.js"></script>
<link rel="stylesheet" href="css/tailwind.css" />
<link rel="stylesheet" href="lib/fonts/fonts.css" />
```
3. For Tailwind: replace `cdn.tailwindcss.com` (runtime JIT, CRIT-01) with a build-time `npx tailwindcss -o wwwroot/css/tailwind.css --minify` step. This also fixes CRIT-01.
4. For fonts: create `wwwroot/lib/fonts/fonts.css` with `@font-face` declarations pointing to local `.woff2` files.
5. Benefits: zero CDN dependency, works fully offline, no SRI needed, faster loads (no DNS lookups), Docker image is self-contained, also resolves HIGH-06 (CSP becomes trivial when all assets are same-origin).

### HIGH-06: No Content-Security-Policy header
**File:** `Program.cs`
**Detail:** The app loads scripts from `cdn.tailwindcss.com`, `unpkg.com`, `fonts.googleapis.com`, and inline `<script>` blocks, but defines no CSP. Any XSS vulnerability (e.g., from a malicious POI name rendered without sanitization) can exfiltrate the entire session.

### HIGH-07: GoogleMapsListScraper is Scoped but runs long-lived browser sessions
**File:** `Program.cs`, line 30; `Services/Import/GoogleMapsListScraper.cs`
**Detail:** The scraper is registered as `Scoped`, which in Blazor Server means per-circuit (per-user session). Each scrape invocation creates a new Playwright instance, launches Chromium, scrapes for potentially minutes, then tears it all down. There is no concurrency limit -- two users hitting "Import" simultaneously will launch two full Chromium instances on the same server. This should be a Singleton service with a `SemaphoreSlim` or a background queue.

### HIGH-08: `LeafletMapService` event handler uses `Action<int>` -- not thread-safe for Blazor Server
**File:** `Services/LeafletMapService.cs`, line 11
**Detail:** `public event Action<int>? OnMarkerClicked;` is a C# event, but Blazor Server circuits run on the thread pool. The `LeafletMap.razor` component subscribes via `HandleMarkerClicked` (an `async void` method at line 60), which can race with component disposal. Use `EventCallback` propagation instead of raw C# events for cross-component communication in Blazor.

---

## MEDIUM

### MED-01: Docker image has no health check
**File:** `Dockerfile`
**Detail:** No `HEALTHCHECK` instruction. Docker/orchestrators cannot detect if the app is hung. Add: `HEALTHCHECK --interval=30s --timeout=5s CMD curl -f http://localhost:8080/ || exit 1` (and install `curl` in the image) or use a `/health` endpoint with `app.MapHealthChecks`.

### MED-02: Docker image runs as root
**File:** `Dockerfile`
**Detail:** No `USER` directive. The container runs as root, which is a container-escape risk. Add a non-root user: `RUN adduser --disabled-password --gecos '' appuser && chown -R appuser /app /data` then `USER appuser`.

### MED-03: Docker image missing Playwright browser dependencies
**File:** `Dockerfile`
**Detail:** The runtime image is `mcr.microsoft.com/dotnet/aspnet:8.0`, which does not include the shared libraries required by Chromium (libx11, libnss3, libatk, etc.). The `GoogleMapsListScraper` will crash with missing library errors in the container. Either install dependencies or (better) separate the scraper.

### MED-04: No response compression middleware
**File:** `Program.cs`
**Detail:** No `app.UseResponseCompression()`. Blazor Server SignalR traffic and static file responses are sent uncompressed. For a map-heavy app that sends marker payloads over the wire, this is measurably wasteful.

### MED-05: No CORS configuration
**File:** `Program.cs`
**Detail:** No CORS policy is defined. While Blazor Server does not typically need CORS for its own circuit, any future API endpoints or webhook callbacks would be unprotected.

### MED-06: SQLite database path constructed via environment variable sniffing
**File:** `Program.cs`, lines 15-18
**Detail:** `Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")` is checked manually instead of using `builder.Environment.IsProduction()` or configuration binding. The connection string should be in `appsettings.json` / `appsettings.Production.json`, not hard-coded with conditional logic in `Program.cs`.

### MED-07: No `appsettings.Production.json`
**File:** `appsettings.json`, `appsettings.Development.json`
**Detail:** There is a development-specific config file but no production one. The production database path, logging levels, and any future configuration overrides have nowhere to live except environment variables.

### MED-08: `downloadFile` JS function defined inline in App.razor
**File:** `Components/App.razor`, lines 49-56
**Detail:** A global `downloadFile` function is embedded in an inline `<script>` block in the HTML root. It should be in `leafletInterop.js` or its own module. Inline scripts also conflict with CSP `script-src` policies.

### MED-09: Global mutable state in `leafletInterop.js`
**File:** `wwwroot/js/leafletInterop.js`
**Detail:** `window.leafletInterop` is a single global object holding `map`, `layerGroups`, `markers`, and `dotnetRef`. This works for a single map instance but will break if the component is ever rendered in multiple places or re-initialized after a circuit reconnect. The `dotnetRef` is overwritten on each `initMap` call without disposing the previous one, which is a .NET reference leak.

### MED-10: Exporters are stateless but registered as Scoped
**File:** `Program.cs`, lines 26-27
**Detail:** `KmlExporter` and `GpxExporter` are pure functions -- they take input and return bytes. They have no injected dependencies, no state. They should be Singleton or, better yet, static utility classes.

### MED-11: `PoiService` is a "God service"
**File:** `Services/PoiService.cs`
**Detail:** Handles collections CRUD, POI CRUD, search, visibility toggling, color updates, and orphan cleanup. This is every database operation in a single class. As the app grows, this will become unmanageable. Split into `CollectionService`, `PoiSearchService`, etc.

### MED-12: `KmlImporter` does not dispose the ZipArchive
**File:** `Services/Import/KmlImporter.cs`, lines 17-21
**Detail:** `var zip = new System.IO.Compression.ZipArchive(...)` is never disposed. The opened stream from `zip.Entries.FirstOrDefault().Open()` also depends on the archive remaining open during XML parsing, creating a tricky lifetime issue. Needs a `using` block with a copy of the inner stream.

### MED-13: Search uses `ToLower().Contains()` -- full table scan on every keystroke
**File:** `Services/PoiService.cs`, lines 96-106
**Detail:** `p.Name.ToLower().Contains(lower)` translates to SQLite's `LOWER(Name) LIKE '%query%'`. This cannot use any index and performs a full table scan across four columns. For hundreds of thousands of POIs this will be painfully slow. Consider an FTS5 virtual table or at minimum a prefix search.

### MED-14: No error boundary in the Blazor component tree
**File:** `Components/Layout/MainLayout.razor`
**Detail:** No `<ErrorBoundary>` wrapping `@Body`. An unhandled exception in any page component will crash the entire circuit with no user-friendly recovery. Blazor 8 provides `<ErrorBoundary>` for exactly this purpose.

### MED-15: `Task.Delay(200)` as map initialization synchronization
**File:** `Components/Pages/MapPage.razor`, line 106
**Detail:** `await Task.Delay(200)` is a race condition pretending to be a solution. On slow connections or under load, 200ms may not be enough. Use a proper callback from JS interop confirming the map is ready.

---

## LOW

### LOW-01: `.dockerignore` excludes `*.md` but not `*.sln`, `*.user`, `.vs/`, `Properties/`
**File:** `.dockerignore`
**Detail:** The `.dockerignore` is minimal. `Properties/launchSettings.json`, `.vs/`, `*.user`, `*.sln`, and test project folders (if any) will be copied into the build context unnecessarily.

### LOW-02: No `GpxExporter` interface
**File:** `Services/Export/GpxExporter.cs`, `Services/Export/KmlExporter.cs`
**Detail:** The exporters are concrete classes with no shared interface. The importers correctly use `IFileImporter`. The exporters should follow the same pattern (`IFileExporter`) for testability and to allow a future export orchestrator.

### LOW-03: Color values hardcoded in multiple places
**File:** `Program.cs` (default color), `ImportOrchestrator.cs` (default "#005bbf"), `SetOperationService.cs` ("#7c3aed"), `DataSourcesPage.razor` (_availableColors array), `PoiCollection.cs` entity default
**Detail:** The color "#005bbf" appears as a default in at least three places. Magic color strings scattered across the codebase make theming changes error-prone. Centralize in a `ThemeConstants` class.

### LOW-04: `Error.razor` uses Bootstrap classes in a Tailwind project
**File:** `Components/Pages/Error.razor`, line 6
**Detail:** `class="text-danger"` is a Bootstrap utility. The rest of the project uses Tailwind. This page will render with unstyled text.

### LOW-05: Entities use `DateTime` instead of `DateTimeOffset`
**File:** `Data/Entities/Poi.cs`, `Data/Entities/PoiCollection.cs`
**Detail:** `AddedDate`, `CreatedDate`, `VisitedDate` are all `DateTime`. In a multi-timezone world, `DateTimeOffset` is the correct type. SQLite stores these as text either way, so there is no storage penalty.

### LOW-06: `PoiCollection.PoiCount` is a denormalized counter maintained manually
**File:** `Data/Entities/PoiCollection.cs`, line 14; `Services/Import/ImportOrchestrator.cs`, line 129
**Detail:** `PoiCount` is set manually after import (`collection.PoiCount = added + skipped`). It is never updated when POIs are deleted, moved, or when collections are modified via operations. It will drift out of sync. Compute it from the join table or maintain it via a trigger/interceptor.

### LOW-07: `OperationsPage.razor` injects `IJSRuntime` and `KmlExporter` directly
**File:** `Components/Pages/OperationsPage.razor`, lines 9-10
**Detail:** The page directly calls `KmlExporter.Export()` and `JS.InvokeVoidAsync("downloadFile", ...)`. Export logic should go through a service. The page is doing data transformation, serialization, and browser interop -- that is three responsibilities too many for a Razor component.

### LOW-08: `Poi.Tags` stored as comma-separated string
**File:** `Data/Entities/Poi.cs`, line 13
**Detail:** Tags as a CSV string means you cannot query "all POIs with tag X" efficiently, cannot enforce uniqueness, and must parse the string on every render. A proper `Tag` entity with a many-to-many join table is the relational way.

### LOW-09: Duplicate URL normalization logic
**File:** `Services/Import/ImportOrchestrator.cs` (`NormalizeGoogleMapsUrl`), `Services/Operations/PoiMatcher.cs` (`NormalizeUrl`)
**Detail:** Two different URL normalization methods in two different classes. The `PoiMatcher` version also lowercases the URL; the `ImportOrchestrator` version does not. These will produce different results for the same input, meaning the dedup operation and the import dedup may disagree on whether two POIs are the same.

### LOW-10: Unused `GpxExporter` registration
**File:** `Program.cs`, line 27
**Detail:** `GpxExporter` is registered as Scoped but never injected anywhere in the codebase. The `OperationsPage` only uses `KmlExporter`. Dead registration.

### LOW-11: No rate limiting on the scraper endpoint
**File:** `Components/Pages/DataSourcesPage.razor`, `ScrapeSharedList` method
**Detail:** A user can rapidly click "Import" to fire off multiple concurrent Playwright sessions. There is no debounce, no semaphore, and no queue. Combined with HIGH-07, this could exhaust server memory.

### LOW-12: `LeafletMap.razor` implements `IDisposable` but contains async operations
**File:** `Components/Shared/LeafletMap.razor`
**Detail:** The component implements `IDisposable` (sync) but its operations are async. It should implement `IAsyncDisposable` and properly await cleanup of the JS interop map instance. The current `Dispose()` only unsubscribes the event handler but does not remove the Leaflet map from the DOM or dispose the `IMapService`.

### LOW-13: `HandleMarkerClicked` is `async void`
**File:** `Components/Shared/LeafletMap.razor`, line 60
**Detail:** `private async void HandleMarkerClicked(int poiId)` -- exceptions thrown here will be unobserved and crash the process. This should flow through `EventCallback` or use proper error handling.

---

## NITPICKS

### NIT-01: Inconsistent naming -- `Poi` vs `POI`
Throughout the codebase, "Poi" is used as a class name (PascalCase), but UI text says "POIs" (all caps). The domain language should be consistent. Either `PointOfInterest` as the full name or `Poi` everywhere.

### NIT-02: `using` directives in `_Imports.razor` include `System.Net.Http` and `System.Net.Http.Json`
These are not used anywhere in the Blazor components (no HTTP client calls). Dead imports.

### NIT-03: Magic numbers throughout
Tolerance defaults of `100` meters, `Take(100)` in search, `Take(200)` in table, `Take(500)` in operations, `50 * 1024 * 1024` file size limit -- none are named constants.

### NIT-04: `app.css` is mostly empty
**File:** `wwwroot/app.css`
The entire custom CSS is 24 lines, two of which are the Blazor error UI boilerplate. All styling is done via Tailwind utility classes. If you fix CRIT-01 with a proper build, `app.css` can absorb the compiled Tailwind output.

### NIT-05: `Routes.razor` uses legacy Blazor routing pattern
The `<Router>` / `<Found>` / `<NotFound>` pattern is the .NET 7 style. .NET 8 introduced the simpler `<Routes>` component with `@attribute [Route]`. This works but is not idiomatic for a .NET 8 project.

---

## ARCHITECTURE ELEGANCE

| Criterion | Score | Rationale |
|-----------|-------|-----------|
| **Project Structure** | 6/10 | Clean folder hierarchy (`Data/Entities`, `Services/Import`, `Services/Export`, `Services/Operations`, `Components/Shared`). Points deducted for no solution file, no test project, no shared constants, and exporters lacking an interface. The structure is reasonable for a solo project but would need reorganization for team development. |
| **Separation of Concerns** | 4/10 | The service layer properly uses `IDbContextFactory` and the import pipeline is well-abstracted with `IFileImporter`. However, Razor pages directly inject `KmlExporter`, `IJSRuntime`, and `PoiService` to perform complex orchestration. The `PoiService` is a monolith. The scraper (an infrastructure concern) lives alongside domain services. Exporters lack abstraction. The JS interop layer is split between `leafletInterop.js` and inline script blocks. Import logic is duplicated wholesale between two methods. |
| **Extensibility** | 6/10 | Adding a new importer is genuinely easy -- implement `IFileImporter`, register it, done. That is good design. But adding a new exporter requires modifying the page that consumes it. Adding a new map provider would require rewriting `LeafletMapService` since the `IMapService` interface is well-defined but the JS interop layer is Leaflet-specific and tightly coupled. Adding authentication would require touching every page. |
| **Maintainability** | 5/10 | The codebase is small (~2000 LOC) and mostly readable. The Tailwind CDN approach means zero build tooling is needed for styling, which is operationally simple but architecturally unsound. The duplicated import logic, scattered magic values, drifting `PoiCount`, dual URL normalization, and lack of tests make confident refactoring impossible. The absence of migrations means any schema change on a live database is manual surgery. |

**Overall Architecture Score: 5.25 / 10**

This is a competent prototype that does what it set out to do. The import pipeline abstraction is genuinely well-designed. But the production readiness gaps (no auth, no migrations, CDN Tailwind, Playwright in the web process, debug file writes) are severe enough that I would block any deployment beyond localhost. Fix the five CRIT items and the project becomes a solid foundation. Ignore them and you have a demo that will bite you the moment someone else touches it.
