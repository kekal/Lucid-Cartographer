# Code Review: Import & Export Services

**Reviewer:** Principal Engineer
**Date:** 2026-04-13
**Scope:** `Services/Import/*`, `Services/Export/*` (14 files)
**Verdict:** The "major fix cycle" left a trail of copy-pasted helper methods, a fake-async CSV importer, sync-over-async exporters, an interface that forgot CancellationToken exists, and enough duplicated XML helpers to make DRY weep. Let's go.

---

## IE-01 | CRITICAL | Sync-over-Async Deadlock Trap in Both Exporters

**Files:** `KmlExporter.cs:18`, `GpxExporter.cs:17`

Both exporters implement `byte[] Export(...)` by calling:

```csharp
ExportAsync(pois, ms, documentName).GetAwaiter().GetResult();
```

This is synchronous blocking on an async method. In ASP.NET (or Blazor Server with a `SynchronizationContext`), this is a classic deadlock vector. The `ExportAsync` method happens to be synchronous internally (returns `Task.CompletedTask`), so it doesn't deadlock *today* -- but the moment someone makes it truly async (e.g., `await doc.SaveAsync(...)`), the entire server hangs with no stack trace to explain why.

**Fix:** Either make `Export` call the synchronous `doc.Save()` directly without going through `ExportAsync`, or delete `Export` entirely and force callers to use `ExportAsync`. The interface should not have both.

**Severity:** Critical -- latent deadlock in production code.

---

## IE-02 | HIGH | `IFileExporter` Missing CancellationToken on All Methods

**File:** `IFileExporter.cs`

```csharp
Task ExportAsync(List<Poi> pois, Stream output, string documentName = "...");
byte[] Export(List<Poi> pois, string documentName = "...");
```

Neither method accepts a `CancellationToken`. For a potentially large POI list being serialized to XML, this is an uninterruptible operation. Contrast with `IFileImporter.ParseAsync`, which correctly accepts `CancellationToken`. The two interfaces were clearly not designed by the same person on the same day.

**Fix:** Add `CancellationToken cancellationToken = default` to `ExportAsync`. Drop `Export` (see IE-01).

**Severity:** High -- export of thousands of POIs cannot be cancelled.

---

## IE-03 | HIGH | `ExportAsync` Is Not Actually Async

**Files:** `KmlExporter.cs:22-36`, `GpxExporter.cs:21-49`

Both implementations build an `XDocument`, call `doc.Save(output)` synchronously, then `return Task.CompletedTask`. This blocks the calling thread for the entire serialization + I/O flush. The `Task` return type is a lie.

The honest thing to do is either use `XmlWriter.CreateAsync` + `doc.WriteToAsync(...)` or stop pretending this is async and change the interface.

**Severity:** High -- blocks thread pool threads under load.

---

## IE-04 | HIGH | Duplicated `FindElement` Helper Across Importers

**Files:** `GpxImporter.cs:55-58`, `KmlImporter.cs:68-71`

Identical method, character-for-character:

```csharp
private static XElement? FindElement(XElement parent, XNamespace ns, string localName)
{
    return parent.Element(ns + localName) ?? parent.Element(localName);
}
```

Copy-pasted into two files. When the next XML-based format arrives (hello, OSM), it will be three copies. The `KmlImporter` also has a `FindDescendant` variant that follows the same pattern.

**Fix:** Extract to a shared `XmlParsingHelpers` static class or a base class for XML-based importers.

**Severity:** High -- DRY violation that will compound.

---

## IE-05 | HIGH | CsvImporter Is Fake-Async

**File:** `CsvImporter.cs:27`

```csharp
await Task.CompletedTask; // satisfy async signature; actual reading is sync below
```

The comment even admits the crime. The entire `ParseAsync` method is synchronous -- it reads the CSV using `csv.Read()` in a blocking while loop. The `async` keyword and `Task` return type exist solely to satisfy the interface.

For a large CSV file, this blocks the calling thread for the entire parse. This is not "async-friendly" as the comment on line 16-17 claims. The honest approach: wrap the synchronous loop in `Task.Run(...)` if you truly cannot make it async, or document that this importer is I/O-blocking.

**Severity:** High -- thread starvation under concurrent imports.

---

## IE-06 | MEDIUM | `IFileExporter` Accepts `List<Poi>` Instead of `IReadOnlyList<Poi>`

**File:** `IFileExporter.cs:22,28`

The interface demands a concrete `List<Poi>`. Exporters only read from it. This forces callers to materialize a `List` even if they have an array, ImmutableList, or any other `IReadOnlyList`. It also communicates the wrong intent -- callers might think the exporter mutates the list.

The import side has the same issue: `IImportOrchestrator.ImportFromScrapedAsync` takes `List<ImportedPoi>`.

**Fix:** Use `IReadOnlyList<Poi>` in the interface. Concrete implementations can accept `IReadOnlyList` too.

**Severity:** Medium.

---

## IE-07 | MEDIUM | `KmlExporter.ExportGroupedByCategory` Is Not on the Interface

**File:** `KmlExporter.cs:38-60`

`ExportGroupedByCategory` is a public method on `KmlExporter` but absent from `IFileExporter`. This means:
1. Callers must downcast to `KmlExporter` to use it, violating the abstraction.
2. If you resolve `IFileExporter` from DI, you cannot access this method.
3. It duplicates the MemoryStream + doc.Save pattern internally instead of calling `ExportAsync`.

Either promote it to the interface (with a default no-op or a separate interface like `ICategorizedExporter`) or remove it.

**Severity:** Medium -- ISP/LSP tension.

---

## IE-08 | MEDIUM | `ImportOrchestrator.GetImporter` Is an Orchestrator Concern Leaked to the Interface

**File:** `IImportOrchestrator.cs:9`

```csharp
IFileImporter? GetImporter(string fileName);
```

This exposes an implementation detail. The orchestrator's job is to orchestrate import; callers should not need to ask "give me the raw importer." If they do, they'll bypass the orchestrator's dedup and persistence logic. This violates encapsulation and invites misuse.

**Fix:** Remove from the interface. If callers need to check "is this file supported?", add `bool CanImport(string fileName)` instead.

**Severity:** Medium -- interface leaks implementation.

---

## IE-09 | MEDIUM | No Input Size Limits on Any Importer

**Files:** All importers

None of the `ParseAsync` implementations validate the stream length before loading the entire document into memory. `XDocument.LoadAsync` and `JsonDocument.ParseAsync` will happily try to parse a 2 GB file, blowing up the server.

The `CsvImporter` is worse -- it reads row by row but has no row count limit, so a malicious CSV with 50 million rows will churn until OOM.

**Fix:** Add configurable max file size checks at the orchestrator level before passing the stream to parsers.

**Severity:** Medium -- denial of service vector.

---

## IE-10 | MEDIUM | `ScrapeResult` Is a Class Defined in the Wrong File

**File:** `GoogleMapsListScraper.cs:5-9`

```csharp
public class ScrapeResult
{
    public string? ListName { get; set; }
    public List<ImportedPoi> Pois { get; set; } = new();
}
```

This is a public DTO stuffed into the top of `GoogleMapsListScraper.cs`, before the scraper class itself. It belongs in its own file (`ScrapeResult.cs`) or at minimum in the interface file. Other code importing `ScrapeResult` has to know to look in `GoogleMapsListScraper.cs`, which is absurd.

Additionally, `ScrapeResult` is a mutable class with a mutable `List<ImportedPoi>`. It should be a record or at least have init-only properties, consistent with `ImportResult` which is already a record.

**Severity:** Medium -- file organization and consistency.

---

## IE-11 | MEDIUM | `KmlImporter.StripHtml` Uses Non-Compiled Regex in a Hot Loop

**File:** `KmlImporter.cs:96-104`

```csharp
var result = Regex.Replace(html, "<[^>]*>", " ");
result = Regex.Replace(result, "<[^>]*>", " ");
```

Called once per Placemark. Uses `Regex.Replace` with a string pattern, which recompiles the regex every call. In .NET 7+ this should use `[GeneratedRegex]` or at minimum a `static readonly Regex` with `RegexOptions.Compiled`.

The double-pass "to catch leftovers from broken tags" is also cargo-cult nonsense. If `<[^>]*>` didn't match a broken tag on the first pass, running the exact same regex again will not magically fix it.

**Severity:** Medium -- unnecessary allocation + the second pass is dead code.

---

## IE-12 | MEDIUM | `ImportOrchestrator` Creates Empty Collection on Parse Failure

**File:** `ImportOrchestrator.cs:74-83`

The collection is created and saved to the database *before* any POIs are validated or persisted:

```csharp
db.PoiCollections.Add(collection);
await db.SaveChangesAsync(cancellationToken);
```

If `validParsed` is empty (all coordinates out of range, or the file was empty), you get an orphaned collection with `PoiCount = 0`. There is no check for "we have zero valid POIs, abort."

**Fix:** Check `validParsed.Count == 0` before creating the collection, or wrap the whole thing in a transaction and roll back.

---

## IE-13 | MEDIUM | `NormalizeGoogleMapsUrl` Is Too Naive

**File:** `ImportOrchestrator.cs:222-231`

The "normalization" is: strip `http://` to `https://`, trim trailing slash. That's it. It does not:
- Remove tracking parameters (`?utm_source=...`)
- Handle URL-encoded characters
- Normalize `maps.google.com` vs `www.google.com/maps`
- Handle short URLs like `maps.app.goo.gl/...`

Two URLs pointing to the same place will fail dedup if one has query params and one doesn't.

**Severity:** Medium -- dedup misses.

---

## IE-14 | MEDIUM | `ExtractCoordinates` in Scraper Has Duplicated `/@` Parsing Logic

**File:** `GoogleMapsListScraper.cs:458-496`

The method first checks for `/@` after `/place/` (lines 466-479), then checks for `/@` anywhere (lines 482-492). The second block is a superset of the first -- if the URL has `/place/.../@...`, the first block matches; if it doesn't have `/place/` but has `/@`, the second block matches. But if both exist, the first block runs and returns, making the second unreachable for that path. The separation adds complexity for no additional coverage.

**Severity:** Medium -- dead code path / unnecessary complexity.

---

## IE-15 | LOW | Inconsistent Default Parameter Name: `name` vs `documentName`

**Files:** `GpxExporter.cs:14,21`, `KmlExporter.cs:15,22`, `IFileExporter.cs:22,28`

The interface uses `documentName`. `GpxExporter.Export` uses `name`. `GpxExporter.ExportAsync` uses `name`. `KmlExporter` uses `documentName`. Same interface, different parameter names across implementations. The compiler doesn't care but humans reading call sites with named arguments will be confused.

**Fix:** Standardize on `documentName` everywhere.

---

## IE-16 | LOW | `GeoJsonImporter` Dead Code Path for Standalone Geometry

**File:** `GeoJsonImporter.cs:48-49`

```csharp
if (geometry.TryGetProperty("type", out var geoType) && geoType.GetString() != "Point")
    return null;
```

This silently drops any feature whose geometry is not `Point` (LineString, Polygon, MultiPoint, etc.). That's arguably correct for a POI importer, but there is zero feedback. A file with 50 polygons returns an empty list and the user has no idea why. At minimum, log or count skipped non-Point features.

Also: if geometry has no `type` property at all, it *passes* this check (because `TryGetProperty` returns false, so the whole condition is false). That means a malformed geometry with coordinates but no type slips through.

**Severity:** Low -- silent data loss.

---

## IE-17 | LOW | `GoogleMapsListScraper` Hardcoded Magic CSS Selectors

**File:** `GoogleMapsListScraper.cs` (throughout)

Selectors like `div.m6QErb.DxyBCb.kA9KIf.dS8AEf`, `span.MW4etd`, `span.UY7F9`, `div.Nv2PK`, etc. are all Google Maps internal CSS class names that can (and do) change without notice. These are scattered as inline string arrays throughout a 510-line method.

They should at minimum be `static readonly` fields with descriptive names, grouped together, so when Google changes their DOM (which happens quarterly), you only need to update one section.

**Severity:** Low -- maintainability, inevitable breakage.

---

## IE-18 | LOW | `ImportOrchestrator` Name-Based Dedup Is Case-Sensitive on One Side

**File:** `ImportOrchestrator.cs:99-107`

```csharp
var importedNames = validParsed
    .Select(p => p.Name.ToLower().Trim())
    .Distinct()
    .ToList();

var existingByName = ...
    .Where(p => importedNames.Contains(p.Name.ToLower()))
    .ToListAsync(cancellationToken)
```

The query uses `p.Name.ToLower()` which, depending on the database provider, may or may not translate to a SQL `LOWER()` call. With SQLite (case-insensitive by default for ASCII), this works accidentally. With PostgreSQL or SQL Server with a case-sensitive collation, `Contains(p.Name.ToLower())` generates a `WHERE LOWER("Name") IN (...)` which may not use indexes.

Also: `ToLower()` without specifying culture is locale-dependent. The infamous Turkish "I" problem.

**Fix:** Use `ToLowerInvariant()` consistently, and verify the EF translation.

---

## IE-19 | LOW | `KmlImporter.ExtractGoogleMapsUrl` Is Fragile URL Parsing

**File:** `KmlImporter.cs:79-93`

This method tries to extract a Google Maps URL from raw HTML by doing string index gymnastics. It searches for `"google.com/maps"`, then walks backward to find `"http"`, then walks forward to find a delimiter. This will break on:
- URLs with `https://www.google.com/maps` (the `LastIndexOf("http", idx)` walks backward from `idx` but the search range is `[0..idx]` which is correct -- but confusing)
- Malformed HTML with multiple URLs on the same line
- URLs inside attribute values with encoded characters

**Severity:** Low -- edge case failures.

---

## IE-20 | LOW | `GpxImporter` Assigns Any Link as `GoogleMapsUrl`

**File:** `GpxImporter.cs:39,44`

```csharp
var linkHref = FindElement(wpt, ns, "link")?.Attribute("href")?.Value;
// ...
GoogleMapsUrl: linkHref,
```

The `<link>` element in GPX can point to anything -- a Wikipedia article, a personal blog, a government website. Blindly stuffing it into `GoogleMapsUrl` is semantically wrong. It will pollute the dedup logic in `ImportOrchestrator` which uses `NormalizeGoogleMapsUrl` on it, and it will be displayed to the user as a "Google Maps link" when it's not.

**Fix:** Only assign to `GoogleMapsUrl` if the href actually contains a Google Maps domain. Otherwise, store it in `Description` or a `Website` field.

---

## IE-21 | LOW | `AsyncEnumerableExtensions.ToHashSetAsync` Belongs Elsewhere

**File:** `ImportOrchestrator.cs:234-245`

A general-purpose EF Core extension method is defined inside the `ImportOrchestrator.cs` file as an `internal static class`. This is a utility that belongs in a shared `Extensions` folder, not buried at the bottom of an orchestrator.

**Severity:** Low -- file organization.

---

## IE-22 | LOW | `ImportResult` Uses `int` for `CollectionId`

**File:** `ImportResult.cs:11`

If the database uses `long`/`bigint` for collection IDs (common in any table that might grow), this will silently truncate. Even if the database uses `int` today, this is a ticking time bomb for a future schema migration.

**Severity:** Low -- type mismatch risk.

---

## IE-23 | LOW | `CsvImporter.FindColumn` Contains-Match Is Overly Greedy

**File:** `CsvImporter.cs:76-88`

The fallback `Contains` match means a column named `"foxylongitude"` matches the `"lon"` candidate (because `"foxylongitude".Contains("lon")` is true). A column named `"explanation"` matches `"x"`. A column named `"category_description"` could match either `"category"` or `"description"` depending on which candidates list runs first.

**Fix:** Use word-boundary matching or at least `StartsWith`/`EndsWith`.

---

## IE-24 | NITPICK | Inconsistent `"Unknown"` Fallback Name

**Files:** `GpxImporter.cs:37`, `KmlImporter.cs:43`, `GeoJsonImporter.cs:61`, `CsvImporter.cs:57`

Three importers fall back to the string `"Unknown"` when no name is found. `CsvImporter` falls back to `$"Point ({lat:F4}, {lon:F4})"` which is actually more useful. The inconsistency means the same unnamed POI imported via GPX shows "Unknown" but via CSV shows "Point (51.5074, -0.1278)".

---

## IE-25 | NITPICK | `IImportOrchestrator` Default Color Duplicated

**Files:** `IImportOrchestrator.cs:12,15`, `ImportOrchestrator.cs:9`

The default color `"#005bbf"` appears as a default parameter in the interface *and* as a `const` in the implementation. If someone changes the const but not the interface default (or vice versa), they diverge silently.

---

---

## Summary Table

| ID | Severity | Category | File(s) | Finding |
|----|----------|----------|---------|---------|
| IE-01 | CRITICAL | Deadlock | KmlExporter, GpxExporter | `GetAwaiter().GetResult()` sync-over-async in `Export()` |
| IE-02 | HIGH | Missing Feature | IFileExporter | No `CancellationToken` on any export method |
| IE-03 | HIGH | Async Violation | KmlExporter, GpxExporter | `ExportAsync` is entirely synchronous |
| IE-04 | HIGH | DRY Violation | GpxImporter, KmlImporter | `FindElement` method duplicated verbatim |
| IE-05 | HIGH | Async Violation | CsvImporter | `await Task.CompletedTask` -- entire method is synchronous |
| IE-06 | MEDIUM | Interface Design | IFileExporter, IImportOrchestrator | `List<T>` instead of `IReadOnlyList<T>` |
| IE-07 | MEDIUM | SOLID (ISP) | KmlExporter | `ExportGroupedByCategory` not on interface |
| IE-08 | MEDIUM | SOLID (ISP) | IImportOrchestrator | `GetImporter` leaks implementation detail |
| IE-09 | MEDIUM | Security/DoS | All importers | No file size or row count limits |
| IE-10 | MEDIUM | Organization | GoogleMapsListScraper | `ScrapeResult` DTO in wrong file, mutable |
| IE-11 | MEDIUM | Performance | KmlImporter | Non-compiled regex + useless second pass |
| IE-12 | MEDIUM | Data Integrity | ImportOrchestrator | Empty collection created when all POIs invalid |
| IE-13 | MEDIUM | Logic | ImportOrchestrator | `NormalizeGoogleMapsUrl` too naive for dedup |
| IE-14 | MEDIUM | Dead Code | GoogleMapsListScraper | Duplicated `/@` coordinate extraction logic |
| IE-15 | LOW | Naming | GpxExporter | `name` vs `documentName` parameter inconsistency |
| IE-16 | LOW | Silent Failure | GeoJsonImporter | Non-Point geometries silently dropped, malformed passes |
| IE-17 | LOW | Maintainability | GoogleMapsListScraper | Magic CSS selectors scattered inline |
| IE-18 | LOW | Correctness | ImportOrchestrator | `ToLower()` locale-dependent, index risk |
| IE-19 | LOW | Fragility | KmlImporter | String-index URL extraction from raw HTML |
| IE-20 | LOW | Semantics | GpxImporter | Any link blindly assigned as GoogleMapsUrl |
| IE-21 | LOW | Organization | ImportOrchestrator | Extension method buried in orchestrator file |
| IE-22 | LOW | Type Safety | ImportResult | `int CollectionId` may truncate |
| IE-23 | LOW | Correctness | CsvImporter | `Contains` column match is overly greedy |
| IE-24 | NITPICK | Consistency | All importers | Inconsistent fallback name for unnamed POIs |
| IE-25 | NITPICK | Consistency | IImportOrchestrator, ImportOrchestrator | Default color duplicated in interface + const |

---

## Elegance Score: 4/10

The import side is structurally sound -- the orchestrator pattern is correct, dedup logic is thoughtful, and CancellationToken flows through the import pipeline. Someone clearly cared about the import path.

The export side looks like it was written in 20 minutes on a Friday. No cancellation support, fake async, sync-over-async traps, and an interface that forgot half its contract. The XML importers share duplicated helpers that scream for a base class. The CSV importer is honest enough to comment its own fakeness but not honest enough to fix it. The scraper is a 500-line Playwright script held together with try/catch and prayer -- which is admittedly the only way to scrape Google Maps, but the `ScrapeResult` DTO shouldn't be squatting in that file.

The codebase has the bones of a well-architected system buried under rushed implementation. Fix the deadlock traps first, then address the interface asymmetry between import and export. The rest is elbow grease.
