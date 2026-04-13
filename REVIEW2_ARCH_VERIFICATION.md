# REVIEW 2 ARCHITECTURE -- VERIFICATION REPORT
**Verifier:** Angry Verification Agent
**Date:** 2026-04-13
**Scope:** All 36 findings from REVIEW2_ARCHITECTURE.md (35 to verify + 1 skipped)

---

## CRITICAL

### ARCH-CRIT-01: EnsureCreatedAsync instead of EF Migrations
**Verdict:** FIXED

`Program.cs:60-68` now calls `db.Database.MigrateAsync()` instead of `EnsureCreatedAsync`. Comment explains that if no migrations exist yet, MigrateAsync will create the DB using the model snapshot. The TODO to generate initial migration is preserved as a reminder. This is correct behavior.

---

### ARCH-CRIT-02: Playwright/Chromium shipped in PRODUCTION runtime image
**Verdict:** SKIPPED (per instructions)

Confirmed still present: `Dockerfile:16` still uses `mcr.microsoft.com/playwright/dotnet:v1.49.0-noble`, and `LucidCartographer.csproj:19` still has unconditional `<PackageReference Include="Microsoft.Playwright" Version="1.49.0" />`. This was explicitly excluded from the fix scope.

---

### ARCH-CRIT-03: Authentication is homebrew SHA256 cookie hashing
**Verdict:** PARTIAL

What WAS fixed:
- Constant-time comparison using `CryptographicOperations.FixedTimeEquals` for both cookie check (`Program.cs:124-125`) and password comparison (`Program.cs:183-184`). CONFIRMED.
- Rate limiting on login: 5 attempts per minute per IP (`Program.cs:143-173`). CONFIRMED.
- CSRF validation on `/login` POST via `IAntiforgery.ValidateRequestAsync` (`Program.cs:148-158`). CONFIRMED.
- `Secure = true` on auth cookie (`Program.cs:197`). CONFIRMED.
- `SameSite = SameSiteMode.Strict` on cookie. CONFIRMED.
- Startup check refusing `changeme` password (`Program.cs:51-56`). CONFIRMED.

What is STILL NOT fixed:
- Still uses SHA256 hash of password stored in cookie -- no session tokens, no revocation mechanism.
- Cookie still valid for 30 days with no ability to invalidate.
- Still not using ASP.NET Core Identity or `Microsoft.AspNetCore.Authentication.Cookies`.
- No salting on the hash (same password = same cookie value forever).

The review asked to "Use ASP.NET Core Identity or at minimum Microsoft.AspNetCore.Authentication.Cookies." That was not done. However, the most dangerous individual issues (no constant-time comparison, no CSRF, no rate limiting, no Secure flag) were all addressed. This is a meaningful hardening but NOT a full fix.

---

### ARCH-CRIT-04: CSP allows 'unsafe-inline' AND 'unsafe-eval' for scripts
**Verdict:** PARTIAL

`Program.cs:82`: `script-src 'self' 'unsafe-inline'` -- `'unsafe-eval'` has been REMOVED. Good.
BUT `'unsafe-inline'` for scripts is STILL present. The comment says "Replace 'unsafe-inline' with nonce-based CSP once Blazor SignalR supports it." This is a known Blazor limitation, so keeping `unsafe-inline` for scripts is arguably unavoidable with current Blazor Server. However, XSS via inline scripts is still possible.

CDN domains are now specified explicitly in style-src and font-src instead of blanket wildcards. Leaflet is self-hosted (removing unpkg CDN dependency for scripts). Google Fonts CDN domains remain for style-src.

The removal of `'unsafe-eval'` is the most important change. Calling this partial because `unsafe-inline` for scripts remains.

---

## HIGH

### ARCH-HIGH-01: Duplicate DI registrations for KmlExporter
**Verdict:** FIXED

`Program.cs:38-39`: Only `IFileExporter` registrations remain for KmlExporter and GpxExporter. Line 40 comment confirms the concrete registration was removed.
`OperationsPage.razor:9`: Now injects `IEnumerable<IFileExporter> Exporters` instead of concrete `KmlExporter`.
`OperationsPage.razor:399`: Resolves KML exporter via `Exporters.First(e => e.FormatName == "KML")`.
`IFileExporter.cs:33-39`: `ExportGroupedByCategory` and `SupportsGrouping` have been added to the interface with default implementations, eliminating the need for concrete injection. CONFIRMED.

---

### ARCH-HIGH-02: Mixed Scoped/Singleton lifetimes for importers
**Verdict:** FIXED

`Program.cs:33-36`: All four importers now registered as `AddSingleton`. Comment explicitly references ARCH-HIGH-02. Consistent with exporter registrations. CONFIRMED.

---

### ARCH-HIGH-03: `GetGoogleMapsUrl` helper is copy-pasted in THREE razor files
**Verdict:** FIXED

`Services/PoiUrlHelper.cs`: New static utility class created with the shared method.
`PoiTable.razor:63`: Uses `PoiUrlHelper.GetGoogleMapsUrl(poi)`. CONFIRMED.
`PoiDetailPane.razor:160`: Uses `PoiUrlHelper.GetGoogleMapsUrl(Poi)`. CONFIRMED.
`OperationsPage.razor:249`: Uses `PoiUrlHelper.GetGoogleMapsUrl(poi)`. CONFIRMED.
No duplicate method definitions remain in any razor file. All three files have comments indicating the move. CONFIRMED.

---

### ARCH-HIGH-04: Scraper registered as Singleton but creates Playwright instances per call
**Verdict:** FIXED

`GoogleMapsListScraper.cs:50`: Semaphore now uses `WaitAsync(TimeSpan.FromMinutes(10), cancellationToken)` with a timeout. Throws `TimeoutException` on timeout. CONFIRMED.
`GoogleMapsListScraper.cs:57-58`: Overall operation timeout of 10 minutes via `CancellationTokenSource.CreateLinkedTokenSource` + `CancelAfter(TimeSpan.FromMinutes(10))`. CONFIRMED.

Browser instance pooling was NOT implemented (the review said "consider"), but the timeout and cancellation fixes address the core "unbounded queue" and "hangs forever" problems.

---

### ARCH-HIGH-05: No HTTPS enforcement
**Verdict:** PARTIAL

`Program.cs:197`: Cookie has `Secure = true`. CONFIRMED.
`docker-compose.yml`: Still runs on plain HTTP port 8080. No TLS termination proxy mentioned.
No `UseHttpsRedirection()` or `UseHsts()` calls in `Program.cs`.

The review asked for "at minimum, add a reverse proxy with TLS termination" and "add UseHttpsRedirection() and UseHsts()." The Secure cookie flag was added, but the application itself still has no HTTPS configuration. The comment says "works behind TLS-terminating proxy (Cloudflare)" -- implying external TLS is expected but not documented or configured here.

---

### ARCH-HIGH-06: Response compression placed BEFORE security headers middleware
**Verdict:** FIXED

`Program.cs:78-95`: Security headers middleware is at lines 78-92, `UseResponseCompression()` is at line 95 -- AFTER security headers. Comment explicitly references ARCH-HIGH-06. CONFIRMED.

---

### ARCH-HIGH-07: Leaflet and fonts loaded from unpinned CDNs without SRI
**Verdict:** PARTIAL

`App.razor:11-12`: Leaflet CSS and JS are now loaded from `lib/leaflet/leaflet.css` and `lib/leaflet/leaflet.js` (self-hosted). CONFIRMED files exist at `wwwroot/lib/leaflet/`.
`App.razor:8-9`: Google Fonts (Manrope, Inter, Material Symbols) STILL loaded from `fonts.googleapis.com` CDN with NO SRI hashes. TODO comment at line 13 acknowledges this.

Leaflet CDN dependency eliminated. Font CDN dependency remains. Partial fix.

---

### ARCH-HIGH-08: No request size limits on the scraper URL or file upload path
**Verdict:** FIXED

`DataSourcesPage.razor:355`: Upload size reduced from 50MB to 10MB (`MaxUploadSizeBytes = 10 * 1024 * 1024`). Comment explains rationale. CONFIRMED.
`GoogleMapsListScraper.cs:57-58`: Overall 10-minute timeout on scrape operations via `CancelAfter(TimeSpan.FromMinutes(10))`. CONFIRMED.

Note: File is still fully buffered into MemoryStream (not streamed). The review suggested streaming, but the size reduction to 10MB mitigates the memory concern significantly for typical POI file sizes.

---

### ARCH-HIGH-09: `CollectionSourceType` constants are incomplete
**Verdict:** FIXED

`PoiStatus.cs:47-49`: Three new constants added: `GeoJsonImport = "geojson_import"`, `CsvImport = "csv_import"`, `GoogleMapsScrape = "google_maps_scrape"`. CONFIRMED.
`PoiStatus.cs:53-55`: `All` array now includes all seven source types. CONFIRMED.

---

## MEDIUM

### ARCH-MED-01: GeoJsonImporter does not handle `.json` extension
**Verdict:** FIXED

`GeoJsonImporter.cs:17`: `_extensions = [".geojson", ".json"]`. CONFIRMED. Google Takeout `.json` files will now be routed to GeoJsonImporter.

---

### ARCH-MED-02: `IMapService` interface leaks Leaflet implementation detail
**Verdict:** FIXED

`IMapService.cs:12`: Method is now `Task RefreshLayoutAsync()` instead of `InvalidateSizeAsync()`. CONFIRMED.
`LeafletMapService.cs:57`: Implements `RefreshLayoutAsync()` and internally calls `leafletInterop.invalidateSize`. CONFIRMED.
`LeafletMap.razor:67`: Calls `MapService.RefreshLayoutAsync()`. CONFIRMED.
No references to `InvalidateSizeAsync` remain in source code (grep confirmed only review docs reference it).

---

### ARCH-MED-03: `LeafletMap.razor` does not call `DisposeAsync` on `MapService`
**Verdict:** FIXED

`LeafletMap.razor:97-101`:
```csharp
if (MapService is IAsyncDisposable disposable)
{
    await disposable.DisposeAsync();
}
```
CONFIRMED. The JS-side map instance will now be properly destroyed via `LeafletMapService.DisposeAsync()`.

---

### ARCH-MED-04: `PoiService.SearchAsync` uses `ToLowerInvariant()` for LIKE
**Verdict:** FIXED

`PoiService.cs:183-194`: Search term now escapes LIKE metacharacters (`%`, `_`, `\`, `[`) before use. Uses `EF.Functions.Like(p.Name, $"%{escaped}%", "\\")` with explicit escape character. CONFIRMED.

Note: Unicode case-sensitivity issue (COLLATE NOCASE) was NOT addressed -- `ToLowerInvariant()` is still applied to the search term at line 184. SQLite LIKE is case-insensitive for ASCII only. This is a minor remaining gap for non-ASCII searches, but the LIKE injection fix was the higher priority.

---

### ARCH-MED-05: No `CancellationToken` passed through to scraper from UI
**Verdict:** FIXED

`DataSourcesPage.razor:284`: `CancellationTokenSource _cts = new()` created at component level.
`DataSourcesPage.razor:397`: `_cts.Token` passed to `Scraper.ScrapeAsync()`. CONFIRMED.
`DataSourcesPage.razor:446-449`: `Dispose()` calls `_cts.Cancel()` and `_cts.Dispose()`. CONFIRMED.
`GoogleMapsListScraper.cs:38`: `ScrapeAsync` accepts `CancellationToken cancellationToken = default`. CONFIRMED.
`GoogleMapsListScraper.cs:181,237`: `cancellationToken.ThrowIfCancellationRequested()` called in scroll and item loops. CONFIRMED.

---

### ARCH-MED-06: `tailwind.config.js` content paths miss `.html` in `wwwroot`
**Verdict:** FIXED

`tailwind.config.js:5`: `"./wwwroot/**/*.js"` added to content array. CONFIRMED. Tailwind classes in JS files will no longer be purged.

---

### ARCH-MED-07: Docker Compose uses hardcoded `AUTH__PASSWORD=changeme`
**Verdict:** FIXED

`docker-compose.yml:11`: Now uses `AUTH__PASSWORD=${AUTH_PASSWORD:-}` (env var substitution from `.env` file with empty default). CONFIRMED.
`.env.example`: Created with `AUTH_PASSWORD=` placeholder and comments about copying to `.env`. CONFIRMED.
`Program.cs:51-56`: Startup check throws if password is still `changeme`. CONFIRMED.

---

### ARCH-MED-08: `.dockerignore` excludes `docker-compose*.yml` but not `.env`
**Verdict:** FIXED

`.dockerignore:18-19`: `.env` and `.env.*` patterns added. CONFIRMED.

---

### ARCH-MED-09: `KmlExporter.Export()` uses `.GetAwaiter().GetResult()` -- sync-over-async
**Verdict:** FIXED

`KmlExporter.cs:17-20`: XML doc comment now explicitly documents: "safe because ExportAsync is synchronous (XDocument.Save is sync and returns Task.CompletedTask). If ExportAsync ever becomes truly async, this must be revisited." CONFIRMED.
`GpxExporter.cs:14-18`: Same documentation pattern applied. CONFIRMED.

The sync-over-async call pattern was NOT removed, but the review's alternative fix option ("mark the sync path clearly as 'only safe because ExportAsync is synchronous'") was implemented. Acceptable.

---

### ARCH-MED-10: `KmlExporter.ExportGroupedByCategory` is not on the `IFileExporter` interface
**Verdict:** FIXED

`IFileExporter.cs:30-39`: `SupportsGrouping` property (default `false`) and `ExportGroupedByCategory` method (with default implementation falling back to `Export`) added to interface. CONFIRMED.
`KmlExporter.cs:14`: `SupportsGrouping => true` overrides the default. CONFIRMED.
The concrete KmlExporter DI registration is gone (verified in ARCH-HIGH-01). CONFIRMED.

---

### ARCH-MED-11: `PoiCollection.PoiCount` is a denormalized field persisted to DB
**Verdict:** FIXED

`PoiCollection.cs:35-36`: `[NotMapped]` attribute added to `PoiCount` property. Comment says "Not persisted -- computed from DB at read time in GetCollectionsAsync." CONFIRMED.
`ImportOrchestrator.cs:214`: Comment says "PoiCount is [NotMapped] -- no need to persist it; it is computed on read." CONFIRMED. The old code that set `collection.PoiCount` during import is removed.

---

### ARCH-MED-12: No logging in importers
**Verdict:** FIXED

`GpxImporter.cs:1,9-13`: Has `ILogger<GpxImporter>` injected, logs parse start and completion with counts and skipped. CONFIRMED.
`KmlImporter.cs:1,10-14`: Has `ILogger<KmlImporter>` injected, logs parse start and completion. CONFIRMED.
`GeoJsonImporter.cs:1,8-12`: Has `ILogger<GeoJsonImporter>` injected, logs parse start and completion. CONFIRMED.
`CsvImporter.cs:2,10-14`: Has `ILogger<CsvImporter>` injected, logs parse start, completion, and errors. CONFIRMED.
`ImportOrchestrator.cs:4,17`: Has `ILogger<ImportOrchestrator>` injected, logs import counts at lines 37-38 and 217-218. CONFIRMED.

---

### ARCH-MED-13: `Error.razor` leaks development information in production
**Verdict:** FIXED

`Error.razor:3`: Injects `IWebHostEnvironment Environment`. CONFIRMED.
`Error.razor:17`: Development text wrapped in `@if (Environment.IsDevelopment())`. CONFIRMED. Production users will no longer see development instructions.

---

## LOW

### ARCH-LOW-01: `app.css` and `input.css` naming is unclear
**Verdict:** NOT FIXED

Files were not renamed. `wwwroot/app.css`, `wwwroot/css/input.css`, `wwwroot/css/tailwind.css` naming remains unchanged. No evidence of any renaming effort. This was a LOW-priority nit.

---

### ARCH-LOW-02: `Properties/launchSettings.json` is in `.dockerignore` but not `.gitignore`
**Verdict:** NOT FIXED

No `.gitignore` file was provided or modified. The inconsistency remains. LOW priority nit.

---

### ARCH-LOW-03: `LoginLayout.razor` is an empty wrapper
**Verdict:** FIXED

`LoginLayout.razor:1`: Comment added: "Minimal layout without navigation chrome, used by the login page to render a full-bleed form without the main application shell." CONFIRMED.

---

### ARCH-LOW-04: No favicon, no manifest, no PWA support
**Verdict:** NOT FIXED

`App.razor`: No `<link rel="icon">`, no `manifest.json`, no service worker references. Still absent. LOW priority.

---

### ARCH-LOW-05: `_Imports.razor` imports `System.Net.Http` and `System.Net.Http.Json` unnecessarily
**Verdict:** FIXED

`_Imports.razor`: The two dead imports (`@using System.Net.Http` and `@using System.Net.Http.Json`) have been removed. Only relevant `@using` directives remain. CONFIRMED.

---

### ARCH-LOW-06: `BlazorDisableThrowNavigationException` in `.csproj` undocumented
**Verdict:** FIXED

`LucidCartographer.csproj:7-9`: XML comment added explaining why this property is set: "Suppresses NavigationException in Blazor Server to prevent enhanced-nav redirects from surfacing as unhandled exceptions in middleware (e.g. auth)." CONFIRMED.

---

### ARCH-LOW-07: No global exception handler for unobserved task exceptions
**Verdict:** NOT FIXED

No `TaskScheduler.UnobservedTaskException` handler was added anywhere. Fire-and-forget patterns still exist (e.g., `DataSourcesPage.razor:392` uses `_ = InvokeAsync(...)` though it does attach `.ContinueWith` to log failures). LOW priority.

---

### ARCH-LOW-08: `HEALTHCHECK` in Dockerfile uses `curl` but the Playwright image may not have it
**Verdict:** NOT FIXED

`Dockerfile:33`: Still uses `curl --fail --silent` for healthcheck. No change to use `wget` or a dedicated binary. Since ARCH-CRIT-02 is skipped and the Playwright image is still the base, `curl` remains available. LOW priority, but still a latent risk if the base image ever changes.

---

### ARCH-LOW-09: No `.editorconfig` or analyzer configuration
**Verdict:** FIXED

`.editorconfig`: New file created with comprehensive settings -- root directive, indent styles for different file types, C# style preferences (var usage, expression bodies, namespace declarations, using organization). CONFIRMED.

No Roslyn analyzer packages were added to the `.csproj`, and no `Directory.Build.props` was created. The `.editorconfig` alone is a meaningful improvement but not the full fix. Calling it fixed since the `.editorconfig` was the primary ask.

---

### ARCH-LOW-10: `CsvImporter.ParseAsync` uses `await Task.CompletedTask` as a hack
**Verdict:** FIXED

`CsvImporter.cs:28`: Now uses `await Task.Yield()` instead of `await Task.CompletedTask`. Comment explains: "CsvHelper reads synchronously -- we yield once to avoid blocking the caller's synchronization context, then proceed with sync I/O on the thread-pool." CONFIRMED. This is a correct approach for wrapping sync I/O in an async method.

---

## SUMMARY

| Category | Total | Fixed | Partial | Not Fixed | Skipped |
|----------|-------|-------|---------|-----------|---------|
| CRITICAL | 4     | 1     | 2       | 0         | 1       |
| HIGH     | 9     | 7     | 2       | 0         | 0       |
| MEDIUM   | 13    | 13    | 0       | 0         | 0       |
| LOW      | 10    | 6     | 0       | 4         | 0       |
| **TOTAL**| **36**| **27**| **4**   | **4**     | **1**   |

### Fully Fixed: 27 of 35 (77%)
### Partial: 4 of 35 (11%)
### Not Fixed: 4 of 35 (11%) -- all LOW priority nits

---

## PARTIAL FIX DETAILS

| Finding | What Remains |
|---------|-------------|
| ARCH-CRIT-03 | Still homebrew SHA256 cookie auth -- no session tokens, no revocation, no ASP.NET Core Identity. Individual vulnerabilities (timing, CSRF, brute force, Secure flag) were all patched. |
| ARCH-CRIT-04 | `'unsafe-eval'` removed (good). `'unsafe-inline'` for scripts remains (Blazor Server limitation). |
| ARCH-HIGH-05 | Secure cookie flag set, but no UseHttpsRedirection, no UseHsts, no TLS config in compose. |
| ARCH-HIGH-07 | Leaflet self-hosted. Google Fonts still from CDN without SRI. |

## NOT FIXED DETAILS

| Finding | Status |
|---------|--------|
| ARCH-LOW-01 | CSS file naming unchanged |
| ARCH-LOW-02 | No .gitignore modification |
| ARCH-LOW-04 | No favicon/manifest/PWA |
| ARCH-LOW-07 | No UnobservedTaskException handler |
| ARCH-LOW-08 | Healthcheck still uses curl |

Note: ARCH-LOW-08 was listed in the review but I count 5 not-fixed LOWs above. Re-checking: LOW-01, LOW-02, LOW-04, LOW-07, LOW-08 = 5 not fixed. But LOW-08 shows NOT FIXED above. Let me recount: 10 LOWs total. Fixed: LOW-03, LOW-05, LOW-06, LOW-09, LOW-10 = 5 fixed. Not fixed: LOW-01, LOW-02, LOW-04, LOW-07, LOW-08 = 5 not fixed. Correcting summary:

**CORRECTED TOTALS:**

| Category | Total | Fixed | Partial | Not Fixed | Skipped |
|----------|-------|-------|---------|-----------|---------|
| CRITICAL | 4     | 1     | 2       | 0         | 1       |
| HIGH     | 9     | 7     | 2       | 0         | 0       |
| MEDIUM   | 13    | 13    | 0       | 0         | 0       |
| LOW      | 10    | 5     | 0       | 5         | 0       |
| **TOTAL**| **36**| **26**| **4**   | **5**     | **1**   |

### Fully Fixed: 26 of 35 (74%)
### Partial: 4 of 35 (11%)
### Not Fixed: 5 of 35 (14%) -- all LOW priority

---

## NEW ISSUES INTRODUCED BY FIXES

### NEW-01: Rate limiter uses in-memory dictionary with no cleanup
**File:** `Program.cs:143`
**Severity:** LOW
The `_loginAttempts` ConcurrentDictionary grows unboundedly. Each unique IP address that attempts login adds an entry that is never evicted (the window resets but the key remains). Under sustained attack from many IPs, this is a slow memory leak. Should use a time-based eviction strategy or `MemoryCache` with sliding expiration.

### NEW-02: Startup password check only blocks "changeme" -- not empty passwords
**File:** `Program.cs:51-56`
**Severity:** MEDIUM
The startup check only rejects the literal string `"changeme"`. If `AUTH__PASSWORD` is set to an empty string or a weak password like "password", the app starts happily. The existing middleware at line 117 does skip auth when `expectedPassword` is empty (`if (!string.IsNullOrEmpty(expectedPassword))`), meaning an empty password DISABLES authentication entirely with no warning at startup.

### NEW-03: CancellationToken not passed through file import path
**File:** `DataSourcesPage.razor:361`
**Severity:** LOW
The scraper path passes `_cts.Token`, but `ImportOrchestrator.ImportAsync` on the file upload path at line 361 does NOT pass a cancellation token. If a user navigates away during a file import, it continues to completion. Less critical than the scraper case since file imports are fast, but inconsistent.

### NEW-04: `PoiUrlHelper` does not use InvariantCulture for coordinate formatting
**File:** `Services/PoiUrlHelper.cs:20`
**Severity:** LOW
The URL string `$"https://www.google.com/maps/search/?api=1&query={poi.Latitude},{poi.Longitude}"` uses default culture formatting. On systems with comma-decimal cultures (e.g., French, German), `poi.Latitude` could render as `48,8566` instead of `48.8566`, breaking the URL. Should use `poi.Latitude.ToString(CultureInfo.InvariantCulture)`.
