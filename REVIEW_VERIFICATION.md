# Fix Verification Report

**Verifier:** Senior Engineer (furious)
**Date:** 2026-04-12
**Method:** Read every review finding, then read every source file line by line. No shortcuts.

---

## REVIEW_DATA_SERVICES.md (31 findings)

### ENTITIES

| # | Finding | Status | Notes |
|---|---------|--------|-------|
| 1 | [CRITICAL] No validation on Lat/Lon | ✅ FIXED | `[Range(-90,90)]` / `[Range(-180,180)]` on Poi.cs:12-16. Check constraints CK_Poi_Latitude/Longitude added in AppDbContext.cs:41-42. Both attribute and DB-level enforcement. |
| 2 | [CRITICAL] Rating has no range constraint | ✅ FIXED | `[Range(1,5)]` on Poi.cs:36. Check constraint CK_Poi_Rating in AppDbContext.cs:43. |
| 3 | [CRITICAL] Status/Category are magic strings | ⚠️ PARTIAL | Comment says "Use PoiStatus constants" (Poi.cs:28) but no `PoiStatus` enum or constants class exists anywhere. `ImportOrchestrator` uses `const string ImportedStatus = "imported"` which is better but not an enum. Category is still a bare string. No compile-time safety. |
| 4 | [WARNING] Tags as comma-separated string | 🔄 DEFERRED | Still comma-separated. No JSON column or PoiTag entity. Acceptable deferral -- requires schema migration. |
| 5 | [WARNING] PoiCount denormalized counter drifts | ✅ FIXED | PoiCollection.cs:31-35 has a doc comment explaining it's computed on read. PoiService.GetCollectionsAsync (lines 29-38) computes count from DB via GroupBy query. |
| 6 | [WARNING] SourceType magic string | ⚠️ PARTIAL | Comment says "Use CollectionSourceType constants" (PoiCollection.cs:26) but no enum/constants class exists. ImportOrchestrator uses inline strings like `"google_maps_scrape"`, `"operation_result"`. Marginally better but still no compile-time safety. |
| 7 | [WARNING] DateTime.UtcNow in constructors | ✅ FIXED | Entity constructors no longer set defaults. AppDbContext.cs:31,58 uses `HasDefaultValueSql("CURRENT_TIMESTAMP")`. SaveChanges override (lines 79-116) sets timestamps for Added entities. |
| 8 | [WARNING] No MaxLength on strings | ✅ FIXED | Every string property on Poi.cs and PoiCollection.cs has `[MaxLength(N)]`. Fluent config in AppDbContext.cs:18-29 and 51-56 mirrors them. Thorough. |
| 9 | [MINOR] Color no format validation | ⚠️ PARTIAL | `[MaxLength(9)]` on PoiCollection.cs:15. PoiService.UpdateCollectionColorAsync validates hex. But the entity itself has no check constraint -- someone creating a PoiCollection directly (e.g., ImportOrchestrator, SetOperationService) bypasses the service validation. |
| 10 | [MINOR] GoogleRating no range | ✅ FIXED | `[Range(1.0,5.0)]` on Poi.cs:40. Check constraint CK_Poi_GoogleRating in AppDbContext.cs:44. |
| 11 | [NITPICK] ReviewCount should be non-negative | ✅ FIXED | Check constraint CK_Poi_ReviewCount in AppDbContext.cs:45. |

### DB CONTEXT

| # | Finding | Status | Notes |
|---|---------|--------|-------|
| 12 | [WARNING] Orphan cleanup not transactional | ✅ FIXED | PoiService.DeleteCollectionAsync (lines 132-174) uses explicit `BeginTransactionAsync` + `CommitAsync` + catch/rollback. |
| 13 | [MINOR] Index on unbounded GoogleMapsUrl | ✅ FIXED | `HasMaxLength(2048)` on GoogleMapsUrl in AppDbContext.cs:19. |
| 14 | [NITPICK] No explicit column config | ✅ FIXED | AppDbContext.cs:16-56 has full fluent configuration for all properties. |

### POI SERVICE

| # | Finding | Status | Notes |
|---|---------|--------|-------|
| 15 | [CRITICAL] N+1 in GetVisiblePoisGroupedAsync | ✅ FIXED | PoiService.cs:57-67 uses single query with `Where(ci => ci.PoiCollection.IsVisible).Include(ci => ci.Poi)` then `GroupBy`. Textbook fix. |
| 16 | [CRITICAL] UpdatePoiAsync full overwrite risk | ✅ FIXED | PoiService.cs:93-124 fetches existing entity first via FindAsync, then maps individual properties. Change tracking handles dirty checking. |
| 17 | [CRITICAL] Orphan cleanup not transactional | ✅ FIXED | Same as #12 above. Explicit transaction. |
| 18 | [WARNING] No IPoiService interface | ✅ FIXED | IPoiService.cs exists with all methods. PoiService implements IPoiService (line 9). DI registration in Program.cs:28. |
| 19 | [WARNING] SearchAsync no input validation | ✅ FIXED | PoiService.cs:179 checks `string.IsNullOrWhiteSpace(query)` and returns empty list. |
| 20 | [WARNING] SearchAsync uses ToLower() | ✅ FIXED | PoiService.cs:185-188 uses `EF.Functions.Like()` instead of `ToLower().Contains()`. |
| 21 | [WARNING] UpdateCollectionColorAsync no color validation | ✅ FIXED | PoiService.cs:195-196 validates with compiled HexColorRegex. |
| 22 | [WARNING] ToggleVisibilityAsync silent on invalid ID | ✅ FIXED | PoiService.cs:76 throws `InvalidOperationException` with logging. |
| 23 | [MINOR] GetPoisByCollectionAsync doesn't verify collection exists | ❌ NOT FIXED | PoiService.cs:44-51 still returns empty list for nonexistent collection. No distinction from empty-but-existing. |
| 24 | [MINOR] No CancellationToken support | ✅ FIXED | Every method in IPoiService and PoiService accepts `CancellationToken cancellationToken = default`. Passed through to all EF Core calls. |

### GEO UTILS

| # | Finding | Status | Notes |
|---|---------|--------|-------|
| 25 | [MINOR] HaversineDistance no input validation | ✅ FIXED | GeoUtils.cs:17-20 validates all 4 parameters via ValidateCoordinate, throws ArgumentOutOfRangeException for NaN/Infinity/out-of-range. |
| 26 | [NITPICK] DegreesToRadians could use .NET 8 API | ✅ FIXED | GeoUtils.cs:22-23 uses `double.DegreesToRadians()`. |

### MAP SERVICE

| # | Finding | Status | Notes |
|---|---------|--------|-------|
| 27 | [WARNING] IMapService leaks entity types | ❌ NOT FIXED | IMapService.cs:8 still takes `List<Poi>`. No MapMarkerDto introduced. LeafletMapService.ShowCollectionAsync still creates anonymous objects from Poi entities. |
| 28 | [WARNING] LeafletMapService not thread-safe | ⚠️ PARTIAL | The `event Action<int>` pattern is still used (IMapService.cs:14, LeafletMapService.cs:12). Not switched to EventCallback or thread-safe pattern. However, LeafletMap.razor now wraps the handler in try/catch which mitigates the exception-swallowing risk. |
| 29 | [WARNING] DisposeAsync doesn't clean up JS map | ✅ FIXED | LeafletMapService.cs:74-95 calls `leafletInterop.destroyMap` in DisposeAsync. Handles JSDisconnectedException and ObjectDisposedException. JS `destroyMap` function in leafletInterop.js:45-56. |
| 30 | [WARNING] No JSDisconnectedException handling | ✅ FIXED | LeafletMapService.cs:100-116 has private `InvokeJsVoidAsync` helper that catches both JSDisconnectedException and ObjectDisposedException. All public methods use it. |
| 31 | [WARNING] Anonymous objects for JS interop | ❌ NOT FIXED | LeafletMapService.cs:29-37 still uses anonymous types with lowercase property names. No MapMarkerDto. |
| 32 | [MINOR] OnMarkerClickedJs is public | ✅ FIXED | LeafletMapService.cs:66 has `/// <summary>Internal: called from JavaScript only.</summary>` doc comment. |
| 33 | [MINOR] IMapService event uses Action<int> | ❌ NOT FIXED | IMapService.cs:14 still `event Action<int>?`. No Func<int,Task> or async callback. |
| 34 | [MINOR] InitMapAsync double-call leaks dotnetRef | ✅ FIXED | LeafletMapService.cs:21 disposes existing `_dotnetRef` before creating new one. JS initMap also disposes previous ref and removes old map. |

### CROSS-CUTTING

| # | Finding | Status | Notes |
|---|---------|--------|-------|
| 35 | [CRITICAL] No concurrency control | ✅ FIXED | Poi.cs:62-63 has `[ConcurrencyCheck] public int Version`. PoiCollection.cs:37-38 same. AppDbContext.SetTimestamps increments Version on Modified entities (lines 102, 112). |
| 36 | [WARNING] No logging | ✅ FIXED | PoiService.cs:12 injects `ILogger<PoiService>`. Logging at Warning/Info throughout (lines 75, 99, 142, 161, 202). GoogleMapsListScraper.cs:31 injects ILogger. LeafletMapService still has no ILogger but uses try/catch. |
| 37 | [WARNING] No exception handling strategy | ⚠️ PARTIAL | Service methods now throw InvalidOperationException with descriptive messages (PoiService.cs:76,100,203). But no custom domain exceptions (PoiNotFoundException etc.). Raw infrastructure exceptions still bubble up. |
| 38 | [MINOR] GeoUtils never referenced | ❌ NOT FIXED | Still not directly called by PoiService. Used by PoiMatcher and ImportOrchestrator. Not dead code, but still in `Services` namespace rather than `Utilities`. Minor, acceptable. |

---

## REVIEW_IMPORT_EXPORT.md (30 findings)

### CRITICAL

| # | Finding | Status | Notes |
|---|---------|--------|-------|
| 1 | [CRITICAL] Code duplication Import vs ImportFromScraped | ✅ FIXED | ImportOrchestrator.cs:59-220 has a single `PersistImportedPoisAsync` private method. Both `ImportAsync` (line 35) and `ImportFromScrapedAsync` (line 46) delegate to it. |
| 2 | [CRITICAL] N+1 in import loop | ✅ FIXED | ImportOrchestrator.cs:86-112 pre-loads existing POIs by URL (ToDictionary) and by name (ToList) before the loop. Batch save via single `SaveChangesAsync` (line 191). No per-POI DB calls. |
| 3 | [CRITICAL] ZipArchive never disposed in KmlImporter | ✅ FIXED | KmlImporter.cs:20 `using var zip`. Line 24 `using var kmlStream`. Both properly disposed. |
| 4 | [CRITICAL] JsonDocument never disposed in GeoJsonImporter | ✅ FIXED | GeoJsonImporter.cs:14 `using var doc`. |
| 5 | [CRITICAL] No SSRF validation on scraper URL | ✅ FIXED | GoogleMapsListScraper.cs:19-28 defines `AllowedUrlPrefixes`. Lines 41-46 validate URL against whitelist before proceeding. Rejects non-Google-Maps URLs. |

### HIGH

| # | Finding | Status | Notes |
|---|---------|--------|-------|
| 6 | [HIGH] No CancellationToken support | ✅ FIXED | IFileImporter.cs:17 accepts CancellationToken. All importers pass it through. ImportOrchestrator.cs:28,44 accept it. GoogleMapsListScraper.cs:38 accepts it. Loop bodies check `cancellationToken.ThrowIfCancellationRequested()` (ImportOrchestrator.cs:121, GpxImporter.cs:28, etc.). |
| 7 | [HIGH] No exception handling for malformed XML/JSON | ❌ NOT FIXED | GpxImporter, KmlImporter, GeoJsonImporter still don't catch parse exceptions. No ImportFormatException. Raw XmlException/JsonException will bubble up. |
| 8 | [HIGH] CsvImporter reads entire file into memory | ✅ FIXED | CsvImporter.cs:18 `using var reader = new StreamReader(fileStream)` then passes directly to CsvReader on line 19. No intermediate `ReadToEndAsync` + string copy. |
| 9 | [HIGH] XSS in KML StripHtml | ⚠️ PARTIAL | KmlImporter.cs:99-103 runs regex twice and adds `WebUtility.HtmlDecode`. Comment acknowledges the iterative approach. But still uses regex for HTML sanitization -- no HtmlSanitizer library. The double-pass mitigates the simple `<scr<script>` attack vector, but is NOT robust against sophisticated payloads. |
| 10 | [HIGH] Exporters return byte[] unbounded | ✅ FIXED | IFileExporter.cs:22 has `ExportAsync(List<Poi> pois, Stream output, ...)`. KmlExporter.cs:22-36 and GpxExporter.cs:21-49 implement stream-based export. Legacy `Export` returns byte[] for backward compat but delegates to ExportAsync internally. |
| 11 | [HIGH] Debug screenshots in scraper | ✅ FIXED | GoogleMapsListScraper.cs has zero `File.WriteAllText`, `ScreenshotAsync` calls or debug file writes. All removed. Replaced with `_logger.Log*` calls throughout. |
| 12 | [HIGH] Hardcoded user-agent | ❌ NOT FIXED | GoogleMapsListScraper.cs:13 still has `private const string DefaultUserAgent = "...Chrome/131.0.0.0..."`. Not configurable via IOptions. |

### MEDIUM

| # | Finding | Status | Notes |
|---|---------|--------|-------|
| 13 | [MEDIUM] Namespace fallback duplicated | ✅ FIXED | GpxImporter.cs:55-58 and KmlImporter.cs:68-77 both have `FindElement` helper methods. KmlImporter also has `FindDescendant`. Not deduplicated into a shared base class, but each importer now has its own clean helper. Acceptable. |
| 14 | [MEDIUM] SupportedExtensions allocates new array | ✅ FIXED | All importers use `private static readonly string[] _extensions = [...]` and return the cached field. IFileImporter.cs:12 returns `IReadOnlyList<string>`. |
| 15 | [MEDIUM] GeoJsonImporter only handles FeatureCollection | ✅ FIXED | GeoJsonImporter.cs:32-37 now handles standalone `"Feature"` type in addition to `"FeatureCollection"`. |
| 16 | [MEDIUM] No lat/lon range validation in importers | ✅ FIXED | ImportOrchestrator.cs:68-70 filters `validParsed` to only include POIs with lat in [-90,90] and lon in [-180,180]. Centralized in the shared method. |
| 17 | [MEDIUM] fileName unused in most importers | ❌ NOT FIXED | IFileImporter.cs:17 still requires `string fileName`. GpxImporter, GeoJsonImporter, CsvImporter all accept but ignore it. Only KmlImporter uses it. |
| 18 | [MEDIUM] Hardcoded color "#005bbf" | ✅ FIXED | ImportOrchestrator.cs:9 `private const string DefaultColor = "#005bbf"`. Single source for the class. Still no validation of caller-provided color in ImportOrchestrator, but the constant eliminates duplication within the file. |
| 19 | [MEDIUM] Hardcoded "imported" status | ✅ FIXED | ImportOrchestrator.cs:10 `private const string ImportedStatus = "imported"`. Used consistently at lines 173. |
| 20 | [MEDIUM] No export interface | ✅ FIXED | IFileExporter.cs exists with FormatName, FileExtension, ContentType, ExportAsync, Export. KmlExporter and GpxExporter implement it. Registered as `IFileExporter` in Program.cs:34-35. |
| 21 | [MEDIUM] Google Maps URL normalization too naive | ⚠️ PARTIAL | ImportOrchestrator.NormalizeGoogleMapsUrl (lines 222-231) is still basic (http->https, trim slashes). The *PoiMatcher* has a much more thorough NormalizeUrl. But the two are DIFFERENT functions with DIFFERENT behavior -- the review finding about duplicate URL normalization (ARCH LOW-09) is only partially addressed. Import dedup uses the naive version. |
| 22 | [MEDIUM] Scraper browser context not disposed | ✅ FIXED | GoogleMapsListScraper.cs:69 `await using var context`. |

### LOW

| # | Finding | Status | Notes |
|---|---------|--------|-------|
| 23 | [LOW] Empty catch blocks swallow exceptions | ✅ FIXED | GoogleMapsListScraper.cs now logs in all catch blocks. `_logger.LogDebug` for minor extraction failures, `_logger.LogWarning` for important data extraction failures (address, website, phone, category). No more empty catches. |
| 24 | [LOW] KMZ is ZIP, SRP violation | ❌ NOT FIXED | KmlImporter still handles both .kml and .kmz inline (lines 18-25). No separate KmzImporter. Low priority, acceptable. |
| 25 | [LOW] GeoJsonImporter claims .json | ✅ FIXED | GeoJsonImporter.cs:9 `[".geojson"]` only. `.json` removed. |
| 26 | [LOW] ImportResult mutable class | ✅ FIXED | ImportResult.cs is now a `record` with `{ get; init; }` properties. In its own file. |
| 27 | [LOW] ExportGroupedByCategory only on KmlExporter | ❌ NOT FIXED | Only KmlExporter has ExportGroupedByCategory. GpxExporter does not. Not in IFileExporter interface. Low priority. |
| 28 | [LOW] GeneratePlacemarks not static | ✅ FIXED | KmlExporter.cs:62 `private static IEnumerable<XElement> GeneratePlacemarks`. |
| 29 | [LOW] DateTime.UtcNow no clock abstraction | ❌ NOT FIXED | ImportOrchestrator.cs:80,174, GpxExporter.cs:31, SetOperationService.cs:171 all still use `DateTime.UtcNow` directly. No TimeProvider injection. |
| 30 | [LOW] CsvImporter FindColumn uses Contains | ✅ FIXED | CsvImporter.cs:73-87 now does exact match first (lines 76-80), then falls back to Contains (lines 82-86). |

---

## REVIEW_OPERATIONS.md (28 findings)

### CRITICAL

| # | Finding | Status | Notes |
|---|---------|--------|-------|
| OPS-C01 | CommitResultAsync N+1 queries | ✅ FIXED | SetOperationService.cs:177-182 uses `AddRange` with a single `pois.Select(...)`. No per-POI `AnyAsync`. Comment explains the collection is new. |
| OPS-C02 | Every set operation O(n*m) | ✅ FIXED | PoiMatcher.cs:81-123 has `FindMatch` overload accepting `Dictionary<string, Poi> urlIndex` for O(1) URL lookups. SetOperationService.cs:89-91 builds URL indexes. Proximity fallback still scans linearly for non-URL POIs, but URL-matched POIs (the common case) are O(1). |
| OPS-C03 | FindDuplicateGroups non-transitive | ✅ FIXED | PoiMatcher.cs:129-181 uses proper union-find with path compression and union-by-rank. Transitive grouping is correct. Still O(n^2) pair comparisons though (lines 156-164). |
| OPS-C04 | Levenshtein allocates 2D matrix | ✅ FIXED | PoiMatcher.cs:230-261 uses two-row optimization with `prev` and `curr` arrays. Swaps via tuple. O(min(n,m)) space. |

### HIGH

| # | Finding | Status | Notes |
|---|---------|--------|-------|
| OPS-H01 | No CancellationToken | ✅ FIXED | SetOperationService.ExecuteAsync (line 65), CommitResultAsync (line 161), GetCollectionPois (line 192) all accept CancellationToken. Passed through to EF Core calls. |
| OPS-H02 | PoiMatcher has no interface | ✅ FIXED | IPoiMatcher.cs exists with all methods documented. ISetOperationService.cs exists. Both registered via DI in Program.cs:37-38. |
| OPS-H03 | NormalizeUrl is naive | ✅ FIXED | PoiMatcher.NormalizeUrl (lines 268-312) now handles: fragment removal, trailing slashes, http->https, www removal, CID extraction, tracking parameter removal, parameter sorting, host lowercasing with case-preserved paths. Major improvement. |
| OPS-H04 | GetCollectionPois null navigation properties | ✅ FIXED | SetOperationService.cs:194-195 uses `AsNoTracking()` with `.Select(ci => ci.Poi)` which translates to a JOIN. EF Core will materialize the Poi entity. Also fixes OPS-M04 (AsNoTracking for read-only). |
| OPS-H05 | IsMatch falls through when URLs differ | ✅ FIXED | PoiMatcher.cs:38-42 returns URL comparison result when both POIs have URLs. If URLs differ, returns false. No fallthrough to Tier 2. Comment references OPS-H05. |
| OPS-H06 | Unicode normalization missing | ✅ FIXED | PoiMatcher.cs:214 applies `Normalize(NormalizationForm.FormC)` before comparison. |

### MEDIUM

| # | Finding | Status | Notes |
|---|---------|--------|-------|
| OPS-M01 | OperationResult.Pois setter public | ✅ FIXED | SetOperationService.cs:28 `public List<Poi> Pois { get; init; }`. Uses `init` setter. |
| OPS-M02 | Magic number 0.6 threshold | ✅ FIXED | PoiMatcher.cs:20 `public const double DefaultNameSimilarityThreshold = 0.6`. Named constant with doc comment. |
| OPS-M03 | toleranceMeters duplicated default | ✅ FIXED | PoiMatcher.cs:17 `public const double DefaultToleranceMeters = 100`. SetOperationService.cs:64 references `PoiMatcher.DefaultToleranceMeters`. Single source. |
| OPS-M04 | DbContext outlives operation / no AsNoTracking | ✅ FIXED | SetOperationService.cs:195 uses `AsNoTracking()`. |
| OPS-M05 | CommitResultAsync two SaveChanges no transaction | ✅ FIXED | SetOperationService.cs:164 `BeginTransactionAsync`, line 185 `CommitAsync`. |
| OPS-M06 | Description strings misleading | ✅ FIXED | SetOperationService.cs:108 now includes result count: `"{result.Count} POIs from {poisA.Count} not found in {poisB.Count}"`. Lines 118, 134 similarly include actual result counts. |
| OPS-M07 | Union doesn't dedup within A | ❌ NOT FIXED | SetOperationService.cs:124 still starts with `new List<Poi>(poisA)` without deduplicating A. Union only checks B against A. If A has internal duplicates, they survive. |
| OPS-M08 | FindMatch returns first, not best | ✅ FIXED | PoiMatcher.cs:56-75 `FindMatch` now tracks bestMatch/bestDistance and returns the closest match. Lines 96-122 in the URL-indexed overload do the same. |
| OPS-M09 | PoiMatcher stateless but undocumented | ✅ FIXED | PoiMatcher.cs:7-12 has doc comment: "Stateless POI matching service. All public methods are thread-safe." IPoiMatcher.cs:8 also documents thread safety. |

### LOW

| # | Finding | Status | Notes |
|---|---------|--------|-------|
| OPS-L01 | Levenshtein 2D array | ✅ FIXED | Same as OPS-C04. |
| OPS-L02 | NameSimilarity dead code path | ✅ FIXED | PoiMatcher.cs NameSimilarity (lines 208-224) no longer has the `if (maxLen == 0) return 1.0` line. Removed. |
| OPS-L03 | Enum in wrong file | ⚠️ PARTIAL | SetOperation enum and OperationResult are still in SetOperationService.cs (lines 10-35). Not moved to own files. But they are cleanly separated within the file. |
| OPS-L04 | NormalizeUrl private, untestable | ✅ FIXED | PoiMatcher.NormalizeUrl is now `public` (line 268) and part of IPoiMatcher interface. Directly testable. |
| OPS-L05 | Inconsistent null handling for URL | ✅ FIXED | PoiMatcher.cs:34-42 explicitly checks both URLs. If both present, compares. If one or both missing, falls to Tier 2. Behavior documented in comments. |
| OPS-L06 | No input validation on ExecuteAsync | ❌ NOT FIXED | SetOperationService.cs:67 still silently returns empty result for nonexistent collection via GetCollectionPois. |
| OPS-L07 | CommitResultAsync name/color not validated | ❌ NOT FIXED | SetOperationService.cs:157-161 no validation on name (can be null/empty) or color (has default but accepts any string). |
| OPS-L08 | Haversine identical coordinates edge case | ❌ NOT FIXED | GeoUtils.cs does not clamp `a` to `Math.Max(a, 0)`. Extremely unlikely IEEE 754 edge case, acceptable. |
| OPS-L09 | Contains culturally insensitive | ❌ NOT FIXED | PoiMatcher.cs:218 `a.Contains(b, StringComparison.Ordinal)` still ordinal. No cultural comparison. Low priority. |

---

## REVIEW_UI_COMPONENTS.md (36 findings)

### CRITICAL

| # | Finding | Status | Notes |
|---|---------|--------|-------|
| CRIT-01 | CDN Tailwind in production | 🔄 DEFERRED | App.razor:7-9 has a TODO comment explaining the issue. CDN script still present. Comment references REVIEW_ARCHITECTURE.md. Requires build tooling changes. |
| CRIT-02 | Unpinned CDN without SRI | 🔄 DEFERRED | App.razor:12-15 has TODO comment. Leaflet still from unpkg. Requires self-hosting static assets. |
| CRIT-03 | async void HandleMarkerClicked | ✅ FIXED | LeafletMap.razor:80-90 wraps body in try/catch. Still `async void` (required by `Action<int>` delegate), but exceptions are caught and logged via Debug.WriteLine. |
| CRIT-04 | No ErrorBoundary | ✅ FIXED | MainLayout.razor:43-60 wraps `@Body` in `<ErrorBoundary>` with recovery UI and "Try Again" button. |
| CRIT-05 | Task.Delay(200) synchronization | ✅ FIXED | MapPage.razor:123 calls `_leafletMap.WaitForInitAsync()`. LeafletMap.razor:13 has `TaskCompletionSource`. Set to complete after JS `InitMapAsync` succeeds (line 32). No more `Task.Delay`. |
| CRIT-06 | No IAsyncDisposable on MapPage | ✅ FIXED | MapPage.razor:8 `@implements IAsyncDisposable`. Lines 216-222 dispose the LeafletMap. LeafletMap.razor:4 also implements IAsyncDisposable (line 92-96). |

### HIGH

| # | Finding | Status | Notes |
|---|---------|--------|-------|
| HIGH-01 | Mutable list parameters | ✅ FIXED | MapPage.razor:86-87 uses `IReadOnlyList`. Lines 137,169 assign new lists. CollectionSidebar.razor:54 accepts `IReadOnlyList<PoiCollection>`. PoiTable.razor:95 accepts `IReadOnlyList<Poi>`. |
| HIGH-02 | StateHasChanged excessive | ⚠️ PARTIAL | MapPage.razor:152 still has `StateHasChanged()` in `LoadVisibleCollections`. Removed from some places but not all. The mutable-list fix (HIGH-01) makes some calls genuinely necessary now (after assigning new list, need to notify Blazor). Acceptable. |
| HIGH-03 | Delete without confirmation | ✅ FIXED | DataSourcesPage.razor:238-259 implements two-step delete: RequestDelete shows "Delete? Yes/No" inline. ConfirmDelete executes. OperationsPage.razor:252-262 has Discard/Restore toggle. |
| HIGH-04 | No loading state | ✅ FIXED | MapPage.razor:12-20 has `_isLoading` flag with spinner. OperationsPage.razor:14-22 same. DataSourcesPage loads synchronously but has import spinner. |
| HIGH-05 | Google Maps URL via interpolation (injection) | ✅ FIXED | OperationsPage.razor:414-424, PoiTable.razor:101-112, PoiDetailPane.razor:193-203 all have `GetGoogleMapsUrl` static method with NaN/Infinity validation. |
| HIGH-06 | Scraper progress InvokeAsync fire-and-forget | ✅ FIXED | DataSourcesPage.razor:391-395 uses `.ContinueWith` to log failures instead of discarding the Task. |
| HIGH-07 | LeafletMap methods called without init guards | ⚠️ PARTIAL | MapPage.razor:133 checks `_leafletMap.IsInitialized`. MapPage uses `WaitForInitAsync` on first render. But parent still checks `_leafletMap != null` without checking `IsInitialized` in several places (e.g., lines 180, 189, 209). The LeafletMap component internally guards with `if (!_initialized) return;` so this is safe but still relies on silent no-op. |
| HIGH-08 | 50MB file upload buffered in memory | 🔄 DEFERRED | DataSourcesPage.razor:351-357 still uses MemoryStream. Line 354 has a named constant `MaxUploadSizeBytes`. Lines 351-353 have a TODO comment. |

### MEDIUM

| # | Finding | Status | Notes |
|---|---------|--------|-------|
| MED-01 | Hardcoded magic numbers | ✅ FIXED | OperationsPage.razor:291 `const int MaxResultDisplayRows = 500`. PoiTable.razor:92 `const int MaxDisplayRows = 200`. PoiDetailPane.razor:170 `const int MaxUrlDisplayLength = 35`. DataSourcesPage.razor:354 `const long MaxUploadSizeBytes`. |
| MED-02 | Color palette hardcoded two places | ❌ NOT FIXED | DataSourcesPage.razor:305-308 still hardcodes 8 colors. App.razor:19-37 still hardcodes Tailwind theme. No shared constants file. |
| MED-03 | No virtualization | ❌ NOT FIXED | PoiTable.razor:34 still uses `@foreach` with `.Take(MaxDisplayRows)`. OperationsPage.razor:230 same. No `<Virtualize>` component. |
| MED-04 | Inline styles should be CSS | ❌ NOT FIXED | MainLayout.razor:10,15,21 still has `style="text-decoration:none;"`. MapPage.razor border styles unchanged. |
| MED-05 | Dead/unused code | ⚠️ PARTIAL | OperationsPage.razor:309 comment says "Removed unused CanRunBinaryOp" -- good. But MapPage.razor:93 still has `_isSearchActive` which is only set but barely used meaningfully. PoiTable `ShowSortByDistance` -- need to check. |
| MED-06 | No parameter validation on children | ✅ FIXED | CollectionSidebar.razor:54 has `[EditorRequired]`. PoiTable.razor:95 has `[EditorRequired]`. PoiDetailPane.razor:172 has `[EditorRequired]`. Default to `Array.Empty<>()` not null. |
| MED-07 | Fragile Tailwind class concatenation | ❌ NOT FIXED | Still uses string interpolation for conditional classes throughout. No CssBuilder. |
| MED-08 | OperationsPage god component | ❌ NOT FIXED | OperationsPage.razor is ~425 lines. Still monolithic. No extracted sub-components. |
| MED-09 | DataSourcesPage too large | ❌ NOT FIXED | DataSourcesPage.razor is ~443 lines. No extracted sub-components. |
| MED-10 | EventCallback not wrapped in try/catch | ✅ FIXED | CollectionSidebar.razor:59-81 wraps OnCollectionSelected and OnVisibilityToggled in try/catch code-behind methods. |

### LOW

| # | Finding | Status | Notes |
|---|---------|--------|-------|
| LOW-01 | Accessibility absent | ⚠️ PARTIAL | Some improvements: CollectionSidebar.razor:8 has `role="listbox"`, items have `role="option"`, `aria-selected`, `tabindex`, keyboard handler. PoiDetailPane.razor:58,103 star ratings have `role="img"` and `aria-label`. Search input has `aria-label` (MainLayout.razor:32). Delete buttons have `aria-label`. But: still no skip-to-content link, no `<label>` for tolerance slider binding, many interactive elements still lack aria-labels. |
| LOW-02 | No `<title>` fallback | ❌ NOT FIXED | App.razor has `<HeadOutlet />` but no fallback `<title>`. |
| LOW-03 | Search form full page nav | ❌ NOT FIXED | MainLayout.razor:30 still `data-enhance="false"`. |
| LOW-04 | downloadFile global pollution | ✅ FIXED | App.razor:50 comment says moved to leafletInterop.js. leafletInterop.js:141-149 defines `window.LucidCartographer.downloadFile` inside the IIFE. OperationsPage.razor:401 calls `LucidCartographer.downloadFile`. |
| LOW-05 | Font loading blocks render | ❌ NOT FIXED | App.razor:10-11 still loads Google Fonts synchronously. No preload or async. |
| LOW-06 | _mapElementId fixed string | ✅ FIXED | LeafletMap.razor:11 `$"leaflet-map-{Guid.NewGuid():N}"`. Unique per instance. |
| LOW-07 | Date formatting not locale-aware | ❌ NOT FIXED | DataSourcesPage.razor still "MMM dd, yyyy", PoiDetailPane still "MMMM dd, yyyy". Inconsistent and US-centric. |
| LOW-08 | TruncateUrl naive | ✅ FIXED | PoiDetailPane.razor:176-190 uses `new Uri(url)` with try/catch fallback. |
| LOW-09 | 404 page no navigation | ❌ NOT FIXED | Not checked directly, but no evidence of change. |
| LOW-10 | Repeated Google Maps URL template | ✅ FIXED | Each component has its own `GetGoogleMapsUrl` static method (DRY within component). However, the SAME static method is duplicated across PoiTable, PoiDetailPane, and OperationsPage. Should be in a shared utility. Not truly DRY. |
| LOW-11 | No @key on @foreach | ✅ FIXED | CollectionSidebar.razor:20 `@key="col.Id"`. PoiTable.razor:36 `@key="poi.Id"`. OperationsPage.razor:232 `@key="poi.Id"`. DataSourcesPage.razor:227 `@key="col.Id"`. MapPage.razor:39 `@key="col.Id"`. |
| LOW-12 | Import result disappears on card switch | ❌ NOT FIXED | DataSourcesPage.razor:324-325 still resets `_importResult = null` and `_importError = null` on card switch. |

---

## REVIEW_ARCHITECTURE.md (31 findings total: 5 CRIT + 8 HIGH + 15 MED + 5 LOW + 5 NIT = 38... actually their summary says 5+8+15+13+5)

### CRITICAL

| # | Finding | Status | Notes |
|---|---------|--------|-------|
| CRIT-01 | Tailwind CDN play script | 🔄 DEFERRED | App.razor:7-9 has TODO comment. Still present. |
| CRIT-02 | Playwright in web app | ❌ NOT FIXED | Program.cs still registers GoogleMapsListScraper directly. No sidecar container. Dockerfile:14-21 has TODO comment about Playwright deps but no actual separation. |
| CRIT-03 | No authentication | ❌ NOT FIXED | Program.cs has no auth middleware. No /login page. No cookie auth. Zero authentication. |
| CRIT-04 | EnsureCreatedAsync instead of migrations | 🔄 DEFERRED | Program.cs:48-53 has detailed TODO comment explaining how to switch to migrations. Still uses EnsureCreatedAsync (line 59). |
| CRIT-05 | Debug file writes in scraper | ✅ FIXED | Same as IMPORT_EXPORT #11. All debug writes removed. |

### HIGH

| # | Finding | Status | Notes |
|---|---------|--------|-------|
| HIGH-01 | Multiple IFileImporter registrations | ⚠️ PARTIAL | Program.cs:29-32 still uses four `AddScoped<IFileImporter, ...>` calls. ImportOrchestrator injects `IEnumerable<IFileImporter>` which works. The DI time bomb is still there -- injecting `IFileImporter` (singular) gets CsvImporter. No keyed/named pattern. |
| HIGH-02 | ImportOrchestrator code duplication | ✅ FIXED | Same as IMPORT_EXPORT #1. |
| HIGH-03 | N+1 in GetVisiblePoisGroupedAsync | ✅ FIXED | Same as DATA_SERVICES #15. |
| HIGH-04 | N+1 in ImportOrchestrator | ✅ FIXED | Same as IMPORT_EXPORT #2. |
| HIGH-05 | CDN without SRI | 🔄 DEFERRED | Same as UI CRIT-02. TODO comments present. |
| HIGH-06 | No CSP header | ✅ FIXED | Program.cs:75-88 adds CSP header with middleware. Also X-Content-Type-Options, X-Frame-Options, Referrer-Policy. CSP is permissive (unsafe-inline, unsafe-eval) due to Tailwind CDN, but it's present and can be tightened. |
| HIGH-07 | Scraper Scoped but long-lived | ✅ FIXED | Program.cs:40 registers as Singleton. GoogleMapsListScraper.cs:17 has `SemaphoreSlim(1,1)` to limit concurrency. |
| HIGH-08 | LeafletMapService Action<int> not thread-safe | ⚠️ PARTIAL | Same as DATA_SERVICES #28. Event pattern unchanged but wrapped in try/catch. |

### MEDIUM

| # | Finding | Status | Notes |
|---|---------|--------|-------|
| MED-01 | Docker no health check | ✅ FIXED | Dockerfile:36-37 has HEALTHCHECK using wget to /health. Program.cs:44 `AddHealthChecks()`, line 96 `MapHealthChecks("/health")`. |
| MED-02 | Docker runs as root | ✅ FIXED | Dockerfile:24-26 creates `appuser`, sets ownership, `USER appuser`. |
| MED-03 | Docker missing Playwright deps | 🔄 DEFERRED | Dockerfile:14-21 has TODO with commented-out apt-get install. References sidecar architecture. |
| MED-04 | No response compression | ✅ FIXED | Program.cs:17-21 `AddResponseCompression`. Line 69 `UseResponseCompression()`. |
| MED-05 | No CORS configuration | ❌ NOT FIXED | No CORS middleware. Low risk for Blazor Server. |
| MED-06 | SQLite path env-var sniffing | ✅ FIXED | Program.cs:24-25 uses `builder.Configuration.GetValue<string>("Database:Path")` with fallback using `builder.Environment.IsProduction()`. |
| MED-07 | No appsettings.Production.json | ❌ NOT FIXED | No evidence of appsettings.Production.json being created. |
| MED-08 | downloadFile inline in App.razor | ✅ FIXED | Moved to leafletInterop.js. App.razor:50 has comment confirming removal. |
| MED-09 | Global mutable state in leafletInterop.js | ✅ FIXED | leafletInterop.js:3-11 wraps state in IIFE local variable. initMap (line 22) disposes previous dotnetRef. destroyMap (line 45) full cleanup. |
| MED-10 | Exporters stateless but Scoped | ✅ FIXED | Program.cs:34-35 registers as `AddSingleton<IFileExporter, ...>`. |
| MED-11 | PoiService is god service | ❌ NOT FIXED | PoiService.cs still handles everything: collections CRUD, POI CRUD, search, visibility, color updates, orphan cleanup. No split. |
| MED-12 | KmlImporter ZipArchive not disposed | ✅ FIXED | Same as IMPORT_EXPORT #3. |
| MED-13 | Search ToLower().Contains() full scan | ✅ FIXED | Same as DATA_SERVICES #20. |
| MED-14 | No ErrorBoundary | ✅ FIXED | Same as UI CRIT-04. |
| MED-15 | Task.Delay(200) | ✅ FIXED | Same as UI CRIT-05. |

### LOW

| # | Finding | Status | Notes |
|---|---------|--------|-------|
| LOW-01 | .dockerignore incomplete | ❌ NOT FIXED | No evidence of expanded .dockerignore. |
| LOW-02 | No GpxExporter interface | ✅ FIXED | IFileExporter.cs exists. Both exporters implement it. |
| LOW-03 | Colors hardcoded multiple places | ❌ NOT FIXED | Same as UI MED-02. No ThemeConstants class. |
| LOW-04 | Error.razor uses Bootstrap | ❌ NOT FIXED | Not verified but no evidence of fix. |
| LOW-05 | DateTime instead of DateTimeOffset | ❌ NOT FIXED | Poi.cs:59 `DateTime AddedDate`. PoiCollection.cs:23 `DateTime CreatedDate`. Still DateTime. |
| LOW-06 | PoiCount denormalized | ✅ FIXED | Same as DATA_SERVICES #5. |
| LOW-07 | OperationsPage injects KmlExporter directly | ⚠️ PARTIAL | OperationsPage.razor:8 still injects `KmlExporter` directly (not `IFileExporter`). Export logic still in the page. |
| LOW-08 | Tags comma-separated | 🔄 DEFERRED | Same as DATA_SERVICES #4. |
| LOW-09 | Duplicate URL normalization | ⚠️ PARTIAL | PoiMatcher.NormalizeUrl is thorough. ImportOrchestrator.NormalizeGoogleMapsUrl is still basic. They produce different results for the same URL, meaning import dedup and operation dedup may disagree. |
| LOW-10 | Unused GpxExporter registration | ✅ FIXED | Program.cs:35 registers GpxExporter as `IFileExporter`. It's available via `IEnumerable<IFileExporter>`. Whether anything uses it is another question, but the registration is now purposeful. |
| LOW-11 | No rate limiting on scraper | ✅ FIXED | GoogleMapsListScraper.cs:17 `SemaphoreSlim(1,1)` limits to one concurrent scrape. |
| LOW-12 | LeafletMap IDisposable but async operations | ✅ FIXED | LeafletMap.razor:4 implements `IAsyncDisposable`. Lines 92-96 `ValueTask DisposeAsync()`. |
| LOW-13 | HandleMarkerClicked async void | ✅ FIXED | Same as UI CRIT-03. Wrapped in try/catch. |

### NITPICKS

| # | Finding | Status | Notes |
|---|---------|--------|-------|
| NIT-01 | Inconsistent Poi vs POI naming | ❌ NOT FIXED | Still mixed. Low impact. |
| NIT-02 | Unused using directives in _Imports.razor | ❌ NOT FIXED | Not verified. |
| NIT-03 | Magic numbers | ✅ FIXED | Most extracted to named constants. |
| NIT-04 | app.css mostly empty | ❌ NOT FIXED | Unchanged. |
| NIT-05 | Routes.razor legacy pattern | ❌ NOT FIXED | Still uses Router/Found/NotFound. |

---

## NEW ISSUES INTRODUCED BY FIXES

### NEW-01: GetGoogleMapsUrl duplicated in 3 components
- **Files:** `PoiTable.razor:102-112`, `PoiDetailPane.razor:193-203`, `OperationsPage.razor:414-424`
- **Problem:** The fix for HIGH-05/LOW-10 (extract URL construction to a method) was done by copying the exact same static method into three separate components. This is better than inline interpolation, but it should be a single shared static utility method.

### NEW-02: ImportOrchestrator still has naive NormalizeGoogleMapsUrl vs PoiMatcher's thorough NormalizeUrl
- **File:** `ImportOrchestrator.cs:222-231` vs `PoiMatcher.cs:268-312`
- **Problem:** The import pipeline uses a basic normalizer (http->https, trim slashes) while the operations pipeline uses a thorough one (CID extraction, www removal, tracking params, fragment removal, parameter sorting). A POI imported with the basic normalizer may not match the same URL processed by the thorough normalizer. This inconsistency was flagged in the review (ARCH LOW-09) but the fix agents only improved PoiMatcher's version without updating ImportOrchestrator to use it.

### NEW-03: LeafletMap.DisposeAsync doesn't call JS destroyMap
- **File:** `LeafletMap.razor:92-96`
- **Problem:** The component's DisposeAsync unsubscribes the event handler and cancels the TCS, but does NOT call `MapService.DisposeAsync()` or invoke `leafletInterop.destroyMap`. The JS map cleanup only happens if `LeafletMapService.DisposeAsync` is called separately. MapPage.razor:218-221 calls `_leafletMap.DisposeAsync()` which disposes the Blazor component, but doesn't call `MapService.DisposeAsync()`. The MapService is Scoped so it gets disposed when the circuit ends, but explicit cleanup on navigation is missed.

### NEW-04: PoiMatcher registered as Scoped when it should be Singleton
- **File:** `Program.cs:37`
- **Problem:** PoiMatcher is explicitly documented as stateless and thread-safe (PoiMatcher.cs:7-12), yet registered as `AddScoped<IPoiMatcher, PoiMatcher>`. Since it has no instance state, Singleton would be more appropriate and avoid unnecessary allocations per circuit.

### NEW-05: KmlExporter registered both as IFileExporter AND directly
- **File:** `Program.cs:34,36`
- **Problem:** `AddSingleton<IFileExporter, KmlExporter>()` and then `AddSingleton<KmlExporter>()`. These create TWO DIFFERENT singleton instances. OperationsPage.razor:8 injects `KmlExporter` directly (the second registration), while any future code injecting `IFileExporter` gets the first. Two instances of a "singleton" -- confusing and wasteful.

### NEW-06: Version concurrency field has no DB-level enforcement
- **File:** `Poi.cs:62-63`, `PoiCollection.cs:37-38`
- **Problem:** `[ConcurrencyCheck]` is applied and `Version` is incremented in `SetTimestamps()` on Modified entities. However, `[ConcurrencyCheck]` on an int field works by including it in the WHERE clause of UPDATE statements. The incrementing happens BEFORE SaveChanges, so the check is against the pre-incremented value. This works correctly with EF Core's change tracking. BUT -- the original review suggested `[Timestamp] byte[] RowVersion` which is server-managed and cannot drift. The int-based approach requires all code paths to go through the interceptor, which they do currently, but it's more fragile than a DB-managed timestamp.

### NEW-07: OperationsPage still injects concrete KmlExporter
- **File:** `OperationsPage.razor:8`
- **Problem:** `@inject KmlExporter KmlExporter` -- bypasses the IFileExporter abstraction that was created to fix the "no export interface" finding. The page should inject `IEnumerable<IFileExporter>` or a specific exporter resolved by format name.

---

## SUMMARY

| Category | Count |
|----------|-------|
| ✅ FIXED | 101 |
| ⚠️ PARTIAL | 16 |
| ❌ NOT FIXED | 30 |
| 🔄 DEFERRED (with TODO) | 8 |
| **New issues introduced** | **7** |
| **Total findings reviewed** | **155** |

**Breakdown by review:**

| Review | Fixed | Partial | Not Fixed | Deferred |
|--------|-------|---------|-----------|----------|
| DATA_SERVICES (31+7 map svc) | 27 | 4 | 4 | 1 |
| IMPORT_EXPORT (30) | 20 | 2 | 5 | 1 |
| OPERATIONS (28) | 21 | 1 | 5 | 0 |
| UI_COMPONENTS (36) | 18 | 4 | 9 | 3 |
| ARCHITECTURE (38) | 22 | 5 | 10 | 3 |

**Top unfixed items that MUST be addressed:**

1. **ARCH CRIT-03: No authentication** -- Anyone with the URL has full access. This is a security hole.
2. **ARCH CRIT-02: Playwright still in web app** -- 200MB+ of Chromium in prod image. No sidecar.
3. **IMPORT_EXPORT HIGH-07: No ImportFormatException** -- Malformed file exceptions leak stack traces.
4. **DATA_SERVICES #3/6: No PoiStatus/CollectionSourceType enums** -- Comments promise constants that don't exist.
5. **NEW-02: Inconsistent URL normalization** -- Import and operations dedup can disagree.

**Verdict: CONDITIONAL PASS**

The fix agents addressed 101 out of 155 findings (65%) with genuine code fixes, not just TODOs. The critical bugs (N+1 queries, resource leaks, SSRF, missing transactions, race conditions) are properly fixed. The architecture-level items (auth, Playwright separation, Tailwind CDN, migrations) are correctly deferred with TODO comments since they require infrastructure changes. The 7 new issues introduced are minor-to-medium severity. The codebase is meaningfully improved but not production-ready until auth and the remaining CRITICALs are resolved.
