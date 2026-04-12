# Code Review: Import & Export Services

**Reviewer:** Principal Engineer (grumpy)
**Date:** 2026-04-12
**Scope:** `Services/Import/*`, `Services/Export/*`

---

## CRITICAL

### [CRITICAL] Massive code duplication between ImportAsync and ImportFromScrapedAsync

**File:** `Services/Import/ImportOrchestrator.cs` (lines 33-140 vs 142-237)

**Problem:** `ImportFromScrapedAsync` is a near-verbatim copy of `ImportAsync`. The entire deduplication logic (Tier 1 Google URL match, Tier 2 name+proximity match, link-or-create) is duplicated across ~100 lines. The only differences are: (1) the source of parsed POIs, (2) `SourceType` string, and (3) `SourceFileName` being absent in the scraped variant.

**Impact:** Every bug fix or behavior change to dedup logic must be applied in two places. This will inevitably drift. It already has: `ImportAsync` sets `SourceFileName`, `ImportFromScrapedAsync` does not.

**Fix:** Extract a private method like `PersistPoisAsync(List<ImportedPoi> parsed, string collectionName, string color, string sourceType, string? sourceFileName)` and have both public methods delegate to it.

---

### [CRITICAL] N+1 database query pattern in import loop

**File:** `Services/Import/ImportOrchestrator.cs` (lines 56-127, duplicated 159-224)

**Problem:** For every single imported POI, the code executes:
1. `FirstOrDefaultAsync` to match by Google Maps URL
2. `Where(...).ToListAsync()` to match by name (pulls ALL name-matched POIs into memory)
3. `AnyAsync` to check if already linked
4. `SaveChangesAsync` for each new POI individually (line 118)

For an import of 500 POIs, that is potentially 2000+ database roundtrips.

**Impact:** Import of large files (thousands of POIs from a Google Takeout GeoJSON) will be painfully slow. Each `SaveChangesAsync` inside the loop also forces a flush and prevents EF Core from batching inserts.

**Fix:** Batch the work. Pre-load existing POIs (by URL and by name) into dictionaries before the loop. Use `AddRange` and call `SaveChangesAsync` once (or in batches of e.g. 100). Use `context.ChangeTracker` to get generated IDs after a single save.

---

### [CRITICAL] Resource leak: ZipArchive never disposed in KmlImporter

**File:** `Services/Import/KmlImporter.cs` (lines 18-22)

**Problem:** When a `.kmz` file is processed, `new ZipArchive(...)` is assigned to a local variable `zip` that is never disposed. The opened `kmlEntry.Open()` stream is also never disposed. Both are `IDisposable`.

**Impact:** File handles and memory held by the zip archive leak until GC finalizer runs. Under load or repeated imports this causes resource exhaustion.

**Fix:** Wrap both in `using` statements. The `xmlStream` from `kmlEntry.Open()` should also be disposed after `XDocument.LoadAsync` completes (or use a `using` block around the load).

---

### [CRITICAL] JsonDocument never disposed in GeoJsonImporter

**File:** `Services/Import/GeoJsonImporter.cs` (line 12)

**Problem:** `JsonDocument.ParseAsync` returns a `JsonDocument` which implements `IDisposable`. It is assigned to `doc` but never disposed. The `JsonElement` values obtained from it become invalid after disposal, but the code finishes using them before returning, so adding `using` is safe here.

**Impact:** Native memory buffers backing the JSON DOM are leaked until GC collects. For large GeoJSON files (multi-MB Google Takeout exports) this is significant.

**Fix:** `using var doc = await JsonDocument.ParseAsync(fileStream);`

---

### [CRITICAL] No input validation on scraper URL -- SSRF vector

**File:** `Services/Import/GoogleMapsListScraper.cs` (line 21)

**Problem:** `ScrapeAsync` takes an arbitrary `listUrl` string and passes it directly to `page.GotoAsync`. There is no validation that the URL is actually a Google Maps URL. An attacker (or confused user) could pass `file:///etc/passwd`, `http://169.254.169.254/latest/meta-data/` (cloud metadata endpoint), or any internal network address.

**Impact:** Server-Side Request Forgery. The headless Chromium browser will happily fetch internal resources, metadata endpoints, or local files and the page content could be logged or returned.

**Fix:** Validate that `listUrl` starts with `https://www.google.com/maps/` or `https://maps.google.com/` before proceeding. Reject all other URLs.

---

## HIGH

### [HIGH] CancellationToken.None hardcoded everywhere -- no cancellation support

**File:** `Services/Import/GpxImporter.cs` (line 12), `Services/Import/KmlImporter.cs` (line 24)

**Problem:** Both XML importers pass `CancellationToken.None` to `XDocument.LoadAsync`. The `IFileImporter.ParseAsync` interface does not accept a `CancellationToken` at all. Neither does `ImportOrchestrator.ImportAsync` or `ScrapeAsync`.

**Impact:** Users cannot cancel a long-running import or scrape. A malicious GPX file with millions of waypoints will block the thread until it completes or OOMs. The scraper with up to 100 scroll iterations (150 seconds) plus per-item click-throughs is completely uncancellable.

**Fix:** Add `CancellationToken cancellationToken = default` to `IFileImporter.ParseAsync`, `ImportOrchestrator.ImportAsync`, `ImportOrchestrator.ImportFromScrapedAsync`, and `IGoogleMapsListScraper.ScrapeAsync`. Thread it through to all async calls and check it in loops.

---

### [HIGH] No exception handling for malformed XML/JSON in importers

**File:** `Services/Import/GpxImporter.cs` (line 12), `Services/Import/KmlImporter.cs` (line 24), `Services/Import/GeoJsonImporter.cs` (line 12)

**Problem:** If the uploaded file contains malformed XML or JSON, `XDocument.LoadAsync` and `JsonDocument.ParseAsync` throw `XmlException` or `JsonException` respectively. These exceptions bubble up unhandled through `ImportOrchestrator.ImportAsync`, which has already created and saved a `PoiCollection` to the database (line 51) before calling `ParseAsync` -- wait, no, parsing happens first. But the raw exceptions still surface to the UI with implementation details.

**Impact:** Unhandled parser exceptions expose internal stack traces. Users get cryptic error messages instead of "Your GPX file is malformed on line 42."

**Fix:** Catch specific parse exceptions in each importer and wrap them in a domain-specific `ImportFormatException` with a user-friendly message and the line/position info from the inner exception.

---

### [HIGH] CsvImporter reads entire file into memory as string

**File:** `Services/Import/CsvImporter.cs` (lines 15-16)

**Problem:** `reader.ReadToEndAsync()` reads the entire CSV stream into a single string in memory, then creates a `StringReader` from that string. This doubles the memory usage (stream buffer + string copy).

**Impact:** A 200MB CSV file will allocate ~400MB+ of managed memory (strings in .NET are UTF-16). This can cause OutOfMemoryException or severe GC pressure on the server.

**Fix:** CsvHelper can read directly from a `StreamReader`. Remove the intermediate string: pass the `StreamReader` directly to `new CsvReader(reader, ...)`.

---

### [HIGH] XSS vulnerability in KML export descriptions

**File:** `Services/Export/KmlExporter.cs` (lines 66-79)

**Problem:** `BuildDescription` concatenates raw POI field values (Address, Category, Notes, GoogleMapsUrl) into a description string without any XML escaping. While `XElement` content is auto-escaped by LINQ to XML, the description is set as text content of an `XElement` on line 57, so it IS escaped. However, the `Notes` field may contain HTML from the KML importer's `StripHtml` (which uses regex -- notoriously unreliable for HTML sanitization). If a POI's notes contain script tags that survived the regex strip, they end up in the exported KML description.

Actually, looking more carefully: `XElement` does escape content, so XSS in the KML XML itself is mitigated by LINQ to XML. But the real issue is:

**File:** `Services/Import/KmlImporter.cs` (line 86)

**Problem:** `StripHtml` uses `Regex.Replace(html, "<[^>]+>", " ")` which is trivially bypassable. Input like `<scr<script>ipt>alert(1)</script>` or unclosed tags `<script` will not be fully stripped. This "sanitized" text is stored in the database as `Notes` and later served to the UI.

**Impact:** Stored XSS if the UI renders description/notes as HTML without further sanitization.

**Fix:** Use a proper HTML sanitizer library (e.g., HtmlSanitizer NuGet) instead of regex. Or use `System.Net.WebUtility.HtmlDecode` on the fully-stripped text and then re-encode on output. Better yet, strip at the UI rendering layer too (defense in depth).

---

### [HIGH] Exporters return byte[] -- unbounded memory allocation

**File:** `Services/Export/KmlExporter.cs` (lines 11, 28), `Services/Export/GpxExporter.cs` (line 10)

**Problem:** Both exporters write to a `MemoryStream` and return `byte[]`. For a collection with 50,000 POIs, the generated XML could be tens of megabytes, all held in a contiguous managed array.

**Impact:** Large exports cause LOH (Large Object Heap) allocations and memory pressure. The `byte[]` must also be held in memory while being written to the HTTP response, doubling usage.

**Fix:** Accept a `Stream` parameter (the response body stream) and write directly to it. This enables streaming and eliminates the intermediate buffer. Signature: `Task ExportAsync(List<Poi> pois, Stream output, ...)`.

---

### [HIGH] Debug artifacts shipped in production code (scraper)

**File:** `Services/Import/GoogleMapsListScraper.cs` (lines 46-49, 85-89, 171-177)

**Problem:** The scraper writes debug screenshots and full HTML dumps to `data/debug_scrape.png`, `data/debug_scrape2.png`, `data/debug_scrape3.png`, `data/debug_scrape.html`, `data/debug_scrape_final.html`. These are hardcoded relative paths with no guard for production vs. development environments.

**Impact:** (1) Disk space leak from accumulated debug files. (2) Full scraped HTML dumped to disk may contain user session data or PII from the Google Maps page. (3) Relative path `data/` depends on working directory, which is unpredictable in production (could write to unexpected locations). (4) Concurrent scrapes overwrite each other's debug files (race condition).

**Fix:** Remove the debug file writes entirely. If debug logging is needed, gate it behind a configuration flag or `ILogger` at Debug level. Never write full page HTML to disk in production.

---

### [HIGH] Hardcoded user-agent string in scraper

**File:** `Services/Import/GoogleMapsListScraper.cs` (line 32)

**Problem:** The Chrome user-agent string `"Mozilla/5.0 ... Chrome/131.0.0.0 ..."` is hardcoded. Chrome 131 will become outdated and Google may start blocking or serving different content to old user-agents.

**Impact:** Scraper silently breaks when Google starts rejecting the stale user-agent. No way to update without recompiling.

**Fix:** Make the user-agent configurable via `IOptions<ScraperOptions>` or similar. Ideally, detect the installed Chromium version from Playwright.

---

## MEDIUM

### [MEDIUM] Namespace fallback logic duplicated across GpxImporter and KmlImporter

**File:** `Services/Import/GpxImporter.cs` (lines 16-23), `Services/Import/KmlImporter.cs` (lines 25-30)

**Problem:** Both XML importers repeat the same pattern: try with namespace, then try without namespace. This "try ns + fallback to no-ns" pattern is duplicated for element lookups (`wpt`/`Placemark`, `name`, `desc`, `coordinates`, `link`, etc.).

**Impact:** Code duplication makes maintenance harder. If a third XML format is added (e.g., OSM XML), the pattern will be copied again.

**Fix:** Extract a helper method like `XElement? FindElement(XElement parent, XNamespace ns, string localName)` that tries both namespaced and non-namespaced lookups.

---

### [MEDIUM] SupportedExtensions allocates a new array on every property access

**File:** `Services/Import/GpxImporter.cs` (line 8), `KmlImporter.cs` (line 9), `GeoJsonImporter.cs` (line 8), `CsvImporter.cs` (line 10)

**Problem:** `public string[] SupportedExtensions => new[] { ".gpx" };` uses an expression-body that allocates a new array every time the property is read. `GetImporter` in `ImportOrchestrator` calls this for every importer on every import.

**Impact:** Minor GC pressure. More importantly, it violates the principle of least surprise -- callers might cache the reference expecting it to be stable, but each call returns a different array instance.

**Fix:** Use a static readonly field or return `IReadOnlyList<string>` backed by a cached array. E.g., `private static readonly string[] _extensions = [".gpx"]; public string[] SupportedExtensions => _extensions;` Or better, change the interface to `IReadOnlyList<string>`.

---

### [MEDIUM] GeoJsonImporter only handles FeatureCollection, not standalone Feature or Geometry

**File:** `Services/Import/GeoJsonImporter.cs` (lines 17-25)

**Problem:** If the root element is a single `Feature` (no `features` array) or a bare `Geometry`, the importer returns an empty list. The comment on line 16 says "Handle both FeatureCollection and direct Feature array" but only `FeatureCollection` is actually handled.

**Impact:** Valid GeoJSON files that contain a single Feature are silently ignored. No error, no warning, just zero results.

**Fix:** Check `root.TryGetProperty("type", ...)` and handle `"Feature"` (single feature), `"Point"` (bare geometry), etc.

---

### [MEDIUM] No latitude/longitude range validation anywhere

**File:** All importers, `ImportOrchestrator.cs`

**Problem:** No importer validates that latitude is in [-90, 90] and longitude is in [-180, 180]. A CSV with `lat=999, lon=-999` will be happily imported and stored.

**Impact:** Garbage data in the database. Map rendering will break or show POIs at impossible locations. The Haversine distance calculation in dedup will produce nonsensical results for out-of-range coordinates.

**Fix:** Add validation in `ImportOrchestrator` (single place) or in each importer. Reject or warn on out-of-range coordinates.

---

### [MEDIUM] `fileName` parameter unused in most importers

**File:** `IFileImporter.cs` (line 7), `GpxImporter.cs`, `GeoJsonImporter.cs`, `CsvImporter.cs`

**Problem:** The `ParseAsync` signature requires `fileName` but only `KmlImporter` uses it (to detect `.kmz`). The other three importers ignore it entirely.

**Impact:** Interface pollution. Callers must provide a filename even when it is meaningless. Suggests the interface was designed around one specific importer's needs.

**Fix:** Remove `fileName` from the interface. For KML/KMZ differentiation, either use separate importers or add a method to detect format from the stream content (magic bytes for ZIP).

---

### [MEDIUM] Hardcoded color default "#005bbf" in ImportOrchestrator

**File:** `Services/Import/ImportOrchestrator.cs` (lines 33, 142)

**Problem:** The default collection color is hardcoded as `"#005bbf"` in two places (duplicated default). There is no validation that the color is a valid hex color.

**Impact:** No way to configure the default without code changes. If someone passes `"not-a-color"`, it is stored as-is and will break CSS rendering in the UI.

**Fix:** Move the default to a configuration constant or `IOptions<ImportOptions>`. Validate the color format (regex for `#[0-9a-fA-F]{6}`).

---

### [MEDIUM] Hardcoded "imported" status string

**File:** `Services/Import/ImportOrchestrator.cs` (lines 114, 207)

**Problem:** `Status = "imported"` is a magic string used in both `ImportAsync` and `ImportFromScrapedAsync`. If the status values are used elsewhere for filtering or display, this is fragile.

**Impact:** Typo in one location silently creates a different status. No compile-time safety.

**Fix:** Use a constants class or enum: `PoiStatus.Imported`.

---

### [MEDIUM] No export interface or abstraction

**File:** `Services/Export/KmlExporter.cs`, `Services/Export/GpxExporter.cs`

**Problem:** Both exporters are concrete classes with no shared interface. `KmlExporter` has `Export` and `ExportGroupedByCategory`; `GpxExporter` has only `Export`. There is no `IExporter` interface, no common contract, no way to add a new format without changing calling code.

**Impact:** Violates OCP (Open/Closed Principle). Adding CSV or GeoJSON export requires modifying the UI/controller layer to know about the new class. Cannot use DI to resolve exporters by format name like importers.

**Fix:** Create `IFileExporter` with `string FormatName`, `string FileExtension`, `byte[] Export(List<Poi> pois, string name)` (or better, the streaming variant). Register all exporters in DI. Resolve by format name, mirroring the import pattern.

---

### [MEDIUM] Google Maps URL normalization is too naive

**File:** `Services/Import/ImportOrchestrator.cs` (lines 239-250)

**Problem:** `NormalizeGoogleMapsUrl` only strips `http://` to `https://` and trailing slashes. It does not handle: query parameter ordering, tracking parameters (utm_*, etc.), URL-encoded vs. decoded characters, or the many different Google Maps URL formats (shortened `goo.gl/maps/...`, `maps.app.goo.gl/...`, place IDs vs. coordinate URLs).

**Impact:** Deduplication by URL will fail for POIs that have the same place but different URL formats. Two imports of the same place will create duplicates.

**Fix:** Parse the URL properly. Extract the place ID (`/place/...`) or CID parameter and normalize on that. At minimum, strip known tracking parameters and sort remaining query params.

---

### [MEDIUM] Scraper browser context not disposed

**File:** `Services/Import/GoogleMapsListScraper.cs` (line 29)

**Problem:** `var context = await browser.NewContextAsync(...)` creates a browser context that is never disposed. The `browser` is disposed via `await using`, but the context should also be disposed to release its resources promptly.

**Impact:** Cookie jars, cache, and other context-scoped resources are leaked until the browser is disposed. Minor in practice since the browser disposal cleans up, but it is sloppy.

**Fix:** `await using var context = await browser.NewContextAsync(...)`.

---

## LOW

### [LOW] Empty catch blocks swallow all exceptions in scraper

**File:** `Services/Import/GoogleMapsListScraper.cs` (lines 80, 89, 177, 245, 260, 271, 283, 305, 315, 348, 359, 372, 379, 415)

**Problem:** At least 14 empty `catch { }` or `catch (Exception) { }` blocks that silently swallow exceptions. Some of these are around critical data extraction (address, website, phone, category).

**Impact:** When scraping fails partially, there is zero diagnostic information. A change in Google's DOM structure will cause silent data loss -- places will be imported without addresses, ratings, etc., and nobody will know why.

**Fix:** At minimum, log at Debug or Trace level in each catch block. For data extraction catches, log at Warning level so operators can detect when Google changes their DOM.

---

### [LOW] `SupportedExtensions` on `KmlImporter` claims `.kmz` but `.kmz` is a ZIP, not XML

**File:** `Services/Import/KmlImporter.cs` (line 9)

**Problem:** KMZ support is bolted onto the KML importer via a file-extension check and inline ZIP handling. This means a single class has two responsibilities: parsing KML XML and extracting from ZIP archives.

**Impact:** SRP violation. Testing KMZ handling requires testing through the KML parser. Cannot reuse the ZIP extraction logic if another format uses ZIP containers.

**Fix:** Either create a separate `KmzImporter` that extracts the KML and delegates to `KmlImporter`, or extract the ZIP handling into a decorator/wrapper.

---

### [LOW] `GeoJsonImporter` claims `.json` extension -- overly broad

**File:** `Services/Import/GeoJsonImporter.cs` (line 8)

**Problem:** `SupportedExtensions` includes `.json`. Any JSON file uploaded will be routed to the GeoJSON importer, even if it is not GeoJSON (e.g., a project config file, a Lottie animation).

**Impact:** Non-GeoJSON `.json` files will be silently parsed as GeoJSON and return zero results (or throw), with a confusing error message.

**Fix:** Remove `.json` from `SupportedExtensions` or add content sniffing (check for `"type": "FeatureCollection"` in the root).

---

### [LOW] `ImportResult` is a mutable class when it should be a record

**File:** `Services/Import/ImportOrchestrator.cs` (lines 7-14)

**Problem:** `ImportResult` is a mutable class with public setters, defined in the same file as `ImportOrchestrator`. It should be immutable (all values are set once at construction time) and in its own file.

**Impact:** Minor. Callers could accidentally mutate the result. File organization makes `ImportResult` hard to find.

**Fix:** Make it a `record` like `ImportedPoi`, or at minimum use `{ get; init; }`. Move to its own file.

---

### [LOW] `ExportGroupedByCategory` only exists on KmlExporter

**File:** `Services/Export/KmlExporter.cs` (line 28)

**Problem:** GPX exporter has no grouped export. If the UI offers "group by category" for export, it only works for KML. This is an inconsistency that will confuse users.

**Impact:** Feature parity gap between export formats.

**Fix:** Either add folder/grouping support to GPX export (GPX doesn't natively support folders, but route grouping could work), or make the UI aware of format capabilities.

---

### [LOW] `GeneratePlacemarks` in KmlExporter is private but not static

**File:** `Services/Export/KmlExporter.cs` (line 52)

**Problem:** `GeneratePlacemarks` does not use any instance state but is not marked `static`. `BuildDescription` IS correctly marked static.

**Impact:** Misleading API. Suggests the method depends on instance state when it does not.

**Fix:** Add `static` modifier.

---

### [LOW] Inconsistent `DateTime.UtcNow` usage -- no clock abstraction

**File:** `Services/Import/ImportOrchestrator.cs` (lines 49, 115, 153, 212), `Services/Export/GpxExporter.cs` (line 21)

**Problem:** `DateTime.UtcNow` is called directly throughout the code. This makes unit testing time-dependent behavior impossible.

**Impact:** Cannot write deterministic tests for `CreatedDate`, `AddedDate`, or GPX metadata timestamps.

**Fix:** Inject `TimeProvider` (or `IClock`) and use it consistently.

---

### [LOW] CsvImporter `FindColumn` uses `Contains` instead of exact match

**File:** `Services/Import/CsvImporter.cs` (lines 69-77)

**Problem:** `headers[i].Contains(c)` means a column named `"my_flatitude"` matches the candidate `"lat"`. A column named `"url_category"` matches `"url"` before `"category"` is checked.

**Impact:** Incorrect column mapping for CSVs with ambiguous header names. Silent data corruption.

**Fix:** Use exact match first (`headers[i] == c`), then fall back to `Contains` only if no exact match was found.

---

### [LOW] Scraper magic numbers not configurable

**File:** `Services/Import/GoogleMapsListScraper.cs`

**Problem:** Hardcoded constants scattered throughout: `30000` (navigation timeout), `5000` (initial wait), `1500` (scroll wait), `100` (max scroll attempts), `3` (stable rounds), `1000` (per-click wait), `10000` (go-back timeout), `1500` (post-back wait). The proximity threshold of `100` meters in `ImportOrchestrator` is also hardcoded.

**Impact:** Cannot tune scraper behavior without recompiling. Different Google Maps regions may need different timeouts.

**Fix:** Extract all magic numbers into a `ScraperOptions` configuration class loaded from `appsettings.json`.

---

## SUMMARY

| Severity | Count |
|----------|-------|
| CRITICAL | 5     |
| HIGH     | 7     |
| MEDIUM   | 9     |
| LOW      | 9     |
| **Total**| **30**|

Top priorities for remediation:
1. Fix the resource leaks (ZipArchive, JsonDocument) -- these are bugs, not style issues.
2. Extract shared persistence logic in ImportOrchestrator to eliminate the copy-paste.
3. Batch database operations to fix the N+1 query disaster.
4. Add CancellationToken support across the board.
5. Validate the scraper URL to prevent SSRF.
6. Remove debug file writes from the scraper.
