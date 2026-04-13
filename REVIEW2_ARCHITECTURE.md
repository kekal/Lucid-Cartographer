# REVIEW 2 -- ARCHITECTURE & INFRASTRUCTURE
**Reviewer:** Principal Architect (FURIOUS)
**Date:** 2026-04-13
**Scope:** Program.cs, .csproj, Docker, configuration, JS interop, project structure, DI, middleware, separation of concerns, extensibility

---

## CRITICAL (Blocks deployment / data loss / security breach)

### ARCH-CRIT-01: EnsureCreatedAsync instead of EF Migrations (STILL OPEN)
**File:** `Program.cs:50-62`
The TODO comment has been sitting here since the last review. `EnsureCreatedAsync` SILENTLY IGNORES schema changes. Add a column to `Poi`? It will never appear in the database. Add an index? Gone. This is not "we'll get to it" territory -- this is "your production database WILL diverge from your model the moment you ship any entity change." The `Microsoft.EntityFrameworkCore.Design` package is ALREADY referenced but NOBODY has run `dotnet ef migrations add`. This is professional negligence.
**Impact:** Silent data model drift, lost schema changes in production.
**Fix:** Generate an initial migration NOW. Replace `EnsureCreatedAsync` with `MigrateAsync`. Add a CI step that fails if the model snapshot is out of date.

### ARCH-CRIT-02: Playwright/Chromium shipped in PRODUCTION runtime image
**File:** `LucidCartographer.csproj:17` / `Dockerfile:16`
`Microsoft.Playwright` (v1.49.0) is an UNCONDITIONAL `<PackageReference>`. The runtime Docker image is `mcr.microsoft.com/playwright/dotnet:v1.49.0-noble` -- a MASSIVE image (~2+ GB) that includes full Chromium, Firefox, and WebKit browsers. This image exists for CI/testing. You are shipping an entire browser fleet as your production base image because ONE feature (Google Maps scraping) needs headless Chromium. Every CVE in Chromium is now your CVE. Every image pull takes minutes. Container startup is bloated.
**Impact:** Enormous attack surface, multi-GB image size, slow deployments, CVE exposure.
**Fix:** (1) Make Playwright a conditional dependency or move scraping to a separate microservice/sidecar. (2) At minimum, use a slim base image and install only Chromium. (3) Consider running Playwright in a separate container with network isolation.

### ARCH-CRIT-03: Authentication is homebrew SHA256 cookie hashing
**File:** `Program.cs:93-156, 170-175`
The "authentication" system is: take a plaintext password from config, SHA256 hash it, store the hash in a cookie, and compare on every request. This is NOT authentication. There is:
- No salting. The same password always produces the same cookie value.
- No HMAC or signing. If an attacker learns the hash (one stolen cookie), they have permanent access.
- No session management. The cookie is valid for 30 days with no revocation mechanism.
- No CSRF on the login POST (the `AntiforgeryToken` is in the Razor form, but the `/login` MapPost endpoint does NOT validate it -- `app.UseAntiforgery()` is placed AFTER the auth middleware but BEFORE `MapPost`, and `MapPost` minimal APIs do NOT automatically validate antiforgery tokens).
- No rate limiting on login attempts. Brute-force is trivial.
- No `Secure` flag on the cookie. It will be sent over HTTP in non-HTTPS contexts.
- The password is checked with `==` (plaintext comparison), not with a constant-time comparison function.
**Impact:** Authentication bypass, session fixation, brute force, cookie theft.
**Fix:** Use ASP.NET Core Identity or at minimum `Microsoft.AspNetCore.Authentication.Cookies` with proper session tokens, CSRF validation, rate limiting, and `Secure` cookie flag.

### ARCH-CRIT-04: CSP allows 'unsafe-inline' AND 'unsafe-eval' for scripts
**File:** `Program.cs:79-81`
The Content-Security-Policy header includes `script-src 'unsafe-inline' 'unsafe-eval'`. This negates essentially ALL XSS protection that CSP provides. The comment says "until we self-host Tailwind and Leaflet" -- but this means the CSP is security theater RIGHT NOW. Combined with the CDN dependencies, any CDN compromise + `unsafe-eval` = full XSS.
**Impact:** XSS protection is effectively disabled.
**Fix:** Self-host all JS/CSS dependencies. Use nonce-based CSP. Remove `unsafe-eval` and `unsafe-inline`.

---

## HIGH (Significant bugs / design flaws / performance hazards)

### ARCH-HIGH-01: Duplicate DI registrations for KmlExporter
**File:** `Program.cs:36-38`
```csharp
builder.Services.AddSingleton<IFileExporter, KmlExporter>();  // line 36
builder.Services.AddSingleton<IFileExporter, GpxExporter>();  // line 37
builder.Services.AddSingleton<KmlExporter>();                 // line 38
```
`KmlExporter` is registered TWICE: once as `IFileExporter` and once as a concrete type. The concrete registration exists solely because `OperationsPage.razor` injects `KmlExporter` directly (`@inject KmlExporter KmlExporter`). This is a DI anti-pattern: the page bypasses the abstraction and hardcodes a dependency on the concrete class. Two separate singleton instances will be created -- one for the interface resolution and one for the concrete resolution.
**Impact:** DI confusion, two instances of what should be a singleton, violation of Dependency Inversion Principle.
**Fix:** Inject `IEnumerable<IFileExporter>` in OperationsPage and select by format name, or create an `IExportService` that wraps format selection. Remove the concrete registration.

### ARCH-HIGH-02: Mixed Scoped/Singleton lifetimes for importers
**File:** `Program.cs:31-34`
All four `IFileImporter` implementations are registered as **Scoped**:
```csharp
builder.Services.AddScoped<IFileImporter, GpxImporter>();
builder.Services.AddScoped<IFileImporter, KmlImporter>();
builder.Services.AddScoped<IFileImporter, GeoJsonImporter>();
builder.Services.AddScoped<IFileImporter, CsvImporter>();
```
But the importers are completely **stateless**. They parse a stream and return a list. There is no reason for them to be Scoped. They should be Singleton, like the exporters. Meanwhile, `ImportOrchestrator` is Scoped and takes `IEnumerable<IFileImporter>` -- this is fine but wasteful because new instances of stateless parsers are created per request scope.
Meanwhile, exporters (`KmlExporter`, `GpxExporter`) ARE registered as Singleton -- proving the team knows stateless services should be Singleton. The inconsistency is maddening.
**Impact:** Unnecessary GC pressure, inconsistent DI lifetime strategy.
**Fix:** Register all stateless importers as Singleton.

### ARCH-HIGH-03: `GetGoogleMapsUrl` helper is copy-pasted in THREE razor files
**Files:** `PoiDetailPane.razor:193-203`, `PoiTable.razor:102-112`, `OperationsPage.razor:414-424`
The EXACT same static method is duplicated in three separate `.razor` files. This is textbook DRY violation. The comments even say "Shared Google Maps URL helper (DRY)" -- the IRONY of labeling a triplicated method as "DRY" is physically painful.
**Impact:** Maintenance burden, risk of divergence when updating logic.
**Fix:** Move to a static utility class (e.g., `PoiUrlHelper.cs`) or a shared base component.

### ARCH-HIGH-04: Scraper registered as Singleton but creates Playwright instances per call
**File:** `GoogleMapsListScraper.cs:64-65`, `Program.cs:42`
`GoogleMapsListScraper` is a Singleton, but every `ScrapeAsync` call does `Playwright.CreateAsync()` and `LaunchAsync()` -- spawning a FULL Chromium process. The SemaphoreSlim limits to one concurrent scrape, but there is no timeout on the semaphore wait, and there is no resource pooling. If a scrape hangs, ALL subsequent scrape requests queue forever.
**Impact:** Potential resource leak, unbounded queue, no timeout.
**Fix:** Add a timeout to `_scrapeSemaphore.WaitAsync()`, implement a circuit breaker or cancellation timeout for the overall scrape operation, consider browser instance pooling.

### ARCH-HIGH-05: No HTTPS enforcement
**File:** `Program.cs`, `Dockerfile`, `docker-compose.yml`
The application runs on plain HTTP (port 8080). There is no `UseHttpsRedirection()`, no HSTS, no TLS termination mentioned anywhere. The auth cookie lacks the `Secure` flag. In production, anyone on the network can sniff the auth cookie and the entire session.
**Impact:** Session hijacking, credential theft over the wire.
**Fix:** At minimum, add a reverse proxy (nginx/Caddy) with TLS termination and set `Secure = true` on cookies. Add `UseHttpsRedirection()` and `UseHsts()` when behind TLS.

### ARCH-HIGH-06: Response compression placed BEFORE security headers middleware
**File:** `Program.cs:71, 77`
`app.UseResponseCompression()` is called at line 71, but the security headers middleware is at line 77. Response compression on HTTPS can enable BREACH attacks. Also, the ordering means compressed responses may not have security headers applied correctly if short-circuited.
**Impact:** Potential BREACH vulnerability, header ordering issues.
**Fix:** Place response compression AFTER security headers. Document the compression vs. TLS tradeoff.

### ARCH-HIGH-07: Leaflet and fonts loaded from unpinned CDNs without SRI
**File:** `App.razor:12-13`
```html
<link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css" />
<script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>
```
No `integrity` attribute. No `crossorigin` attribute. The TODO comment acknowledges this. unpkg.com is a community CDN that has had availability issues. A CDN compromise means arbitrary JS execution in your app. Google Fonts (`fonts.googleapis.com`, `fonts.gstatic.com`) are also external with no SRI.
**Impact:** Supply-chain attack vector, single point of failure.
**Fix:** Self-host Leaflet and fonts in `wwwroot/lib/`. Add SRI hashes if CDN use is kept.

### ARCH-HIGH-08: No request size limits on the scraper URL or file upload path
**Files:** `DataSourcesPage.razor:354-358`, `GoogleMapsListScraper.cs`
The file upload caps at 50MB but buffers the ENTIRE file into a `MemoryStream`. With Blazor Server, each circuit (user connection) holds this in server memory. 10 concurrent 50MB uploads = 500MB of server RAM just for upload buffers. The scraper has no timeout on the overall operation -- a Google Maps list with 10,000 items could run for hours.
**Impact:** Memory exhaustion, denial of service.
**Fix:** Stream file processing without full buffering. Add an overall timeout to scrape operations (e.g., 10 minutes max).

### ARCH-HIGH-09: `CollectionSourceType` constants are incomplete
**File:** `PoiStatus.cs:42-53`
`CollectionSourceType.All` contains `GpxImport, KmlImport, Manual, OperationResult` -- but the actual source types used in code include `"geojson_import"`, `"csv_import"`, `"google_maps_scrape"` (set by `ImportOrchestrator.cs:39, 49`). The constants list is missing three valid source types. This means `All` is a LIE.
**Impact:** Validation based on `All` would reject valid data, misleading API contracts.
**Fix:** Add the missing constants: `GeoJsonImport`, `CsvImport`, `GoogleMapsScrape`.

---

## MEDIUM (Code quality / maintainability / minor performance issues)

### ARCH-MED-01: GeoJsonImporter does not handle `.json` extension
**File:** `GeoJsonImporter.cs:9`
`SupportedExtensions` is `[".geojson"]` only. But `DataSourcesPage.razor:291` shows the takeout card accepts `.geojson,.json` files. Google Takeout exports `Saved Places.json` -- a `.json` file. The `ImportOrchestrator.GetImporter()` matches by extension, so `.json` files will return `null` and fail to import. The feature described in the UI DOES NOT WORK.
**Impact:** Google Takeout import (a primary feature) is broken for `.json` files.
**Fix:** Add `".json"` to `GeoJsonImporter.SupportedExtensions`.

### ARCH-MED-02: `IMapService` interface leaks Leaflet implementation detail
**File:** `IMapService.cs:12`
The interface has `InvalidateSizeAsync()` -- this is a Leaflet-specific concept. A generic map service interface should not expose Leaflet's internal resize API. If you ever swap to MapLibre GL or Google Maps JS API, this method name is meaningless.
**Impact:** Tight coupling between abstraction and implementation.
**Fix:** Rename to something generic like `RefreshLayoutAsync()` or remove if only used internally.

### ARCH-MED-03: `LeafletMap.razor` does not call `DisposeAsync` on `MapService`
**File:** `LeafletMap.razor:92-96`
The `DisposeAsync` method unsubscribes the event handler and cancels the TCS, but does NOT call `await ((IAsyncDisposable)MapService).DisposeAsync()`. The `LeafletMapService` implements `IAsyncDisposable` and needs its `DisposeAsync` to be called to clean up the JS-side map (`destroyMap`). The parent `MapPage.razor` calls `_leafletMap.DisposeAsync()` which hits `LeafletMap.DisposeAsync()`, but that never propagates to `LeafletMapService.DisposeAsync()`.
**Impact:** JS-side memory leak -- the Leaflet map instance is never destroyed on circuit disconnect unless the service is disposed by the DI container at scope end.
**Fix:** Call `MapService.DisposeAsync()` (if it implements `IAsyncDisposable`) in `LeafletMap.DisposeAsync()`.

### ARCH-MED-04: `PoiService.SearchAsync` uses `ToLowerInvariant()` for LIKE -- wrong for SQLite
**File:** `PoiService.cs:184`
```csharp
var lower = query.ToLowerInvariant();
return await db.Pois
    .Where(p => EF.Functions.Like(p.Name, $"%{lower}%") ...)
```
SQLite's `LIKE` is already case-insensitive for ASCII characters. But `ToLowerInvariant()` is applied to the SEARCH TERM, not to the column data. So if the DB has "Caf\u00e9 PARIS" and you search "caf\u00e9 paris", the `LIKE` will compare the lowered search against the original-case column. SQLite LIKE is case-insensitive for A-Z only, NOT for Unicode. This will miss accented character matches. Also, the search term is interpolated directly into the LIKE pattern without escaping `%` and `_` metacharacters in the user input.
**Impact:** Incorrect search results for Unicode text, potential LIKE injection.
**Fix:** Use `COLLATE NOCASE` or SQLite's `lower()` function. Escape LIKE metacharacters in user input.

### ARCH-MED-05: No `CancellationToken` passed through to scraper from UI
**File:** `DataSourcesPage.razor:386`
`Scraper.ScrapeAsync(_sharedListUrl, count => {...})` passes no `CancellationToken`. If the user navigates away or the circuit disconnects, the scraper keeps running a headless Chromium instance indefinitely. The entire scrape (which can take 10+ minutes for large lists) is fire-and-forget.
**Impact:** Wasted resources, zombie Chromium processes.
**Fix:** Create a `CancellationTokenSource` tied to component disposal, pass its token to `ScrapeAsync`.

### ARCH-MED-06: `tailwind.config.js` content paths miss `.html` in `wwwroot`
**File:** `tailwind.config.js:4`
```js
content: ["./Components/**/*.razor", "./wwwroot/**/*.html"]
```
But there ARE no `.html` files in `wwwroot`. The template file is `App.razor` (which IS covered by the first glob). However, the Tailwind build DOES NOT scan `wwwroot/**/*.js` -- so any Tailwind classes used in JS (e.g., dynamically generated HTML in `leafletInterop.js`) would be purged. Currently the JS uses inline styles so this is not actively broken, but it is a latent trap.
**Impact:** Latent -- Tailwind classes in JS would be purged silently.
**Fix:** Add `"./wwwroot/**/*.js"` to the content array.

### ARCH-MED-07: Docker Compose uses hardcoded `AUTH__PASSWORD=changeme`
**File:** `docker-compose.yml:11`
The default password is literally `changeme`. If someone deploys with the provided compose file without changing it, the application is "protected" by a password that is publicly visible in the repository. The compose file should reference an `.env` file or document that this MUST be changed.
**Impact:** Default credentials in production.
**Fix:** Use `${AUTH_PASSWORD:-}` with a `.env` file. Add a startup check that refuses to start if the password is still `changeme`.

### ARCH-MED-08: `.dockerignore` excludes `docker-compose*.yml` but not `.env`
**File:** `.dockerignore`
If a `.env` file with secrets is created alongside the Dockerfile (as recommended for Docker Compose), it will be included in the build context and potentially baked into the image layer cache.
**Impact:** Secret leakage via Docker image layers.
**Fix:** Add `.env` and `.env.*` to `.dockerignore`.

### ARCH-MED-09: `KmlExporter.Export()` uses `.GetAwaiter().GetResult()` -- sync-over-async
**File:** `KmlExporter.cs:18`, `GpxExporter.cs:17`
```csharp
public byte[] Export(...) {
    using var ms = new MemoryStream();
    ExportAsync(pois, ms, documentName).GetAwaiter().GetResult();
    return ms.ToArray();
}
```
Sync-over-async can cause deadlocks in ASP.NET contexts with synchronization contexts. The `ExportAsync` methods happen to return `Task.CompletedTask` (since `XDocument.Save` is sync), so it technically works -- but the pattern is fragile. If `ExportAsync` ever becomes truly async, this will deadlock.
**Impact:** Fragile pattern, potential deadlock on future changes.
**Fix:** Remove the sync `Export` method or mark the sync path clearly as "only safe because ExportAsync is synchronous."

### ARCH-MED-10: `KmlExporter.ExportGroupedByCategory` is not on the `IFileExporter` interface
**File:** `KmlExporter.cs:38-60`
`ExportGroupedByCategory` is a public method on `KmlExporter` that is NOT part of the `IFileExporter` interface. This means it can only be called if you inject the concrete `KmlExporter` type (which is why the concrete registration exists in DI -- see ARCH-HIGH-01). This circular dependency between the DI hack and the non-interface method is architectural rot.
**Impact:** Leaky abstraction, DI workaround.
**Fix:** Either add grouped export to the interface or extract it into a separate service.

### ARCH-MED-11: `PoiCollection.PoiCount` is a denormalized field persisted to DB
**File:** `PoiCollection.cs:35`, `PoiService.cs:30-38`
`PoiCount` is stored in the database but then OVERWRITTEN from a live count query in `GetCollectionsAsync`. This means the DB value is always stale and misleading. During import (`ImportOrchestrator.cs:209`), it IS set -- but this value immediately becomes wrong if POIs are added/removed outside the import flow. Storing a value that is always overwritten on read is pointless database writes.
**Impact:** Misleading persisted data, wasted write I/O.
**Fix:** Make `PoiCount` a `[NotMapped]` property computed only at read time, or remove it from the entity and use a ViewModel/DTO.

### ARCH-MED-12: No logging in importers
**Files:** `GpxImporter.cs`, `KmlImporter.cs`, `GeoJsonImporter.cs`, `CsvImporter.cs`
All four importers have ZERO logging. If a file fails to parse or produces unexpected results, there is no diagnostic trail. The `ImportOrchestrator` also lacks logging (no `ILogger` injected). Only `PoiService` and `GoogleMapsListScraper` have loggers.
**Impact:** Debugging import failures requires attaching a debugger.
**Fix:** Inject `ILogger<T>` into each importer and the orchestrator. Log parse counts, skipped entries, and errors.

### ARCH-MED-13: `Error.razor` leaks development information in production
**File:** `Error.razor:15-25`
The error page unconditionally shows text about "Development environment" and how to enable it. This instructional text should not appear in production -- it reveals internal architecture details.
**Impact:** Information disclosure.
**Fix:** Wrap the development instructions in an `@if (HttpContext?.RequestServices.GetService<IWebHostEnvironment>()?.IsDevelopment() == true)` check.

---

## LOW (Nits / style / minor improvements)

### ARCH-LOW-01: `app.css` and `input.css` serve different purposes but naming is unclear
`wwwroot/app.css` has base styles and Blazor error UI. `wwwroot/css/input.css` is the Tailwind entry point. `wwwroot/css/tailwind.css` is the generated output. The naming convention is inconsistent and confusing. Consider `styles/base.css`, `styles/tailwind-input.css`, `styles/tailwind-output.css`.

### ARCH-LOW-02: `Properties/launchSettings.json` is in `.dockerignore` but not `.gitignore`
It contains development URLs and ports. It SHOULD be in version control (it is developer configuration), but the inconsistency between ignore files is worth noting.

### ARCH-LOW-03: `LoginLayout.razor` is an empty wrapper
**File:** `LoginLayout.razor`
The entire file is `@inherits LayoutComponentBase` + `@Body`. It exists solely to provide a layout-less layout for the login page. This is fine but could be documented with a comment explaining why it exists.

### ARCH-LOW-04: No favicon, no manifest, no PWA support
**File:** `App.razor`
The HTML head has no `<link rel="icon">`, no `manifest.json`, no service worker. For a map application that users might want to use on mobile, PWA support would be a natural fit.

### ARCH-LOW-05: `_Imports.razor` imports `System.Net.Http` and `System.Net.Http.Json` unnecessarily
**File:** `_Imports.razor:1-2`
These are for HTTP client usage, but the app uses Blazor Server with no `HttpClient` calls. Dead imports that add confusion.

### ARCH-LOW-06: `BlazorDisableThrowNavigationException` in `.csproj` undocumented
**File:** `LucidCartographer.csproj:7`
`<BlazorDisableThrowNavigationException>true</BlazorDisableThrowNavigationException>` is set but not explained. This suppresses `NavigationException` in Blazor Server, which affects how `NavigationManager.NavigateTo` works. A comment explaining why this is needed would help.

### ARCH-LOW-07: No global exception handler for unobserved task exceptions
There is no `TaskScheduler.UnobservedTaskException` handler. The `async void HandleMarkerClicked` in `LeafletMap.razor` has try/catch, but other fire-and-forget patterns (like the scrape progress callback) could swallow exceptions silently.

### ARCH-LOW-08: `HEALTHCHECK` in Dockerfile uses `curl` but the Playwright image may not have it
**File:** `Dockerfile:33`
The healthcheck uses `curl`, which is available in the Playwright image. But if the base image ever changes to a slimmer variant, `curl` might not be present. Using `wget` or a dedicated health check binary would be more portable.

### ARCH-LOW-09: No `.editorconfig` or analyzer configuration
There is no `.editorconfig`, no `Directory.Build.props` with analyzer settings, no Roslyn analyzer packages. Code style consistency relies entirely on developer discipline.

### ARCH-LOW-10: `CsvImporter.ParseAsync` uses `await Task.CompletedTask` as a hack
**File:** `CsvImporter.cs:27`
```csharp
await Task.CompletedTask; // satisfy async signature
```
If the method is not actually async, either make it sync and wrap at the call site, or use `Task.Run` for the sync CSV reading to avoid blocking the calling thread. The current approach blocks synchronously despite the async signature.

---

## ARCHITECTURE SUMMARY

### What was done well
- Clean separation of `Data/`, `Services/`, `Components/` directories
- Interface-driven DI for services (IPoiService, IFileImporter, IFileExporter, etc.)
- `IDbContextFactory` usage for Blazor Server (avoids DbContext concurrency issues)
- Proper `IAsyncDisposable` implementation on `LeafletMapService`
- Union-find algorithm for transitive duplicate grouping is solid
- Concurrency control on scraper via SemaphoreSlim
- IIFE wrapper on JS interop to avoid global pollution
- XSS protection via `escapeHtml()` in JS marker popups

### What is structurally wrong
1. **Security is amateur-hour:** Homebrew auth, no HTTPS, CDN dependencies with no SRI, CSP is disabled via unsafe-eval.
2. **Playwright as a production dependency is an architectural mistake.** The scraper should be a separate service or at minimum a separate container.
3. **No migration strategy** means the first schema change after deployment will silently fail.
4. **DI lifetime inconsistency** (Scoped stateless services, duplicate registrations, concrete type injection) shows lack of DI discipline.
5. **DRY violations** (three copies of `GetGoogleMapsUrl`) alongside comments claiming DRY compliance.
6. **No logging in the import pipeline** -- the most complex and error-prone part of the application has zero observability.

---

## ARCHITECTURE ELEGANCE SCORES

| Category               | Score | Rationale |
|------------------------|-------|-----------|
| **Structure**          | 6/10  | Directory layout is clean (Data/Services/Components/Shared), but no clear separation between features. All services are in flat folders. No DTOs -- entities used end-to-end from DB to UI. |
| **Separation of Concerns** | 5/10  | Service interfaces exist but are violated (concrete KmlExporter injection, GetGoogleMapsUrl scattered across views). Auth logic is inline in Program.cs. JS interop mixes map logic and file download. |
| **Extensibility**      | 5/10  | Importer/Exporter pattern is genuinely extensible (add a new IFileImporter, done). But auth is not pluggable, export format selection requires DI hacks, and the scraper is tightly coupled to Playwright/Chromium. |
| **Maintainability**    | 4/10  | No migrations, no logging in importers, triplicated helper methods, fragile sync-over-async patterns, dead imports, no analyzers, no tests visible in the project, GeoJSON importer silently rejects .json files breaking a primary workflow. |
| **Overall**            | **5.0/10** | The bones are there -- interface-driven services, factory-based DbContext, proper component hierarchy. But the execution is undermined by security shortcuts, DI sloppiness, missing logging, and the Playwright elephant in the runtime image. |
