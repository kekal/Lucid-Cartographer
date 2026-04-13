# Code Review: Data Layer & Core Services

**Reviewer:** Principal Engineer (Angry Mode)
**Date:** 2026-04-13
**Scope:** Entities, AppDbContext, PoiService, GeoUtils, IMapService, LeafletMapService

---

## Findings

---

### [CRITICAL] SQL Injection via EF.Functions.Like in SearchAsync

**File:** `Services/PoiService.cs` (lines 183-188)
**Problem:** The `query` string is interpolated directly into the LIKE pattern without escaping LIKE wildcard characters (`%`, `_`, `[`). A user searching for `%` or `_` gets unintended pattern matching. While EF Core parameterizes the value (so this is not raw SQL injection), unescaped LIKE metacharacters allow wildcard abuse -- a search for `%` matches every row, and `_` matches any single character. This is a semantic injection into the LIKE pattern.
**Impact:** Data exfiltration of all POIs via a single-character search; potential DoS by forcing full table scans on large datasets.
**Fix:** Escape LIKE wildcards before interpolation: replace `%` with `[%]`, `_` with `[_]`, `[` with `[[]` (SQLite syntax). Or use a dedicated search method that sanitizes wildcards.

---

### [CRITICAL] Denormalized PoiCount Field is a Consistency Timebomb

**File:** `Data/Entities/PoiCollection.cs` (line 35), `Services/PoiService.cs` (lines 29-39)
**Problem:** `PoiCollection.PoiCount` is a persistent column that gets written during imports but is recomputed from the join table only in `GetCollectionsAsync`. Any code path that reads a `PoiCollection` without going through `GetCollectionsAsync` (e.g., `FindAsync`, direct queries, other services) will see stale/wrong counts. The field is writable with no guard, so any code can set it to garbage.
**Impact:** Incorrect POI counts displayed to users; confusing bugs when other code paths trust the persisted value.
**Fix:** Either (a) make `PoiCount` a `[NotMapped]` property and always compute it, or (b) maintain it transactionally via a trigger or in every mutation method. The current hybrid approach is the worst of both worlds.

---

### [HIGH] No Validation of Status/Category Values Before Persistence

**File:** `Services/PoiService.cs` (lines 93-124), `Data/Entities/Poi.cs` (lines 28, 26)
**Problem:** `UpdatePoiAsync` blindly copies `poi.Status` and `poi.Category` to the existing entity. `PoiStatus.IsValid()` and `PoiCategory.All` exist but are never called anywhere in the service layer. The entity has no check constraint for valid status/category values either. Any arbitrary string will be persisted.
**Impact:** Corrupt data; UI may break on unexpected status/category values; filtering/grouping logic may silently drop records.
**Fix:** Validate `poi.Status` against `PoiStatus.IsValid()` and `poi.Category` against `PoiCategory.All` in `UpdatePoiAsync` (and any future create methods). Throw `ArgumentException` on invalid values.

---

### [HIGH] Missing Validation in UpdatePoiAsync -- No Coordinate/Name Checks

**File:** `Services/PoiService.cs` (lines 93-124)
**Problem:** The method accepts a raw `Poi` object and copies every property without validating anything. Latitude/longitude outside [-90,90]/[-180,180], empty Name, negative ReviewCount -- all sail right through. The `[Range]` and `[MaxLength]` attributes on the entity are only enforced if explicit model validation is triggered (e.g., in MVC/Blazor form validation), not by EF Core on save.
**Impact:** Invalid coordinates, empty names, or oversized strings may be persisted. The DB check constraints will catch range violations, but the exception will be an opaque `DbUpdateException` instead of a clear validation error.
**Fix:** Add explicit validation in the service method before applying changes. At minimum: non-empty Name, valid coordinate ranges, non-negative ReviewCount, MaxLength checks on string fields.

---

### [HIGH] PoiCollection.Color Accepts 9-Character Strings but Regex Only Validates 7

**File:** `Data/Entities/PoiCollection.cs` (line 16), `Services/PoiService.cs` (line 14)
**Problem:** The `Color` property has `[MaxLength(9)]` allowing up to 9 characters, but the `HexColorRegex` in PoiService only validates `#RRGGBB` (7 chars). This means the DB schema allows `#RRGGBBAA` (8-char hex with alpha) but the service rejects it. Conversely, the MaxLength(9) is misleading -- it suggests 9 characters are valid when the regex says otherwise. The `[MaxLength(9)]` is also repeated in `OnModelCreating` (line 53 of AppDbContext).
**Impact:** Confusing inconsistency. If someone tries to store an RGBA color, the service rejects it even though the DB would accept it. Or if the regex is the source of truth, the MaxLength should be 7.
**Fix:** Decide on one format. If only `#RRGGBB` is valid, set `[MaxLength(7)]`. If `#RRGGBBAA` should be supported, update the regex accordingly.

---

### [HIGH] Version Increment in SetTimestamps Conflicts with ConcurrencyCheck

**File:** `Data/AppDbContext.cs` (lines 91-116)
**Problem:** The `SetTimestamps` method increments `Version` for all modified entities. But `[ConcurrencyCheck]` on `Version` means EF Core includes the original `Version` value in the UPDATE WHERE clause. If the entity was loaded with Version=3 and `SetTimestamps` bumps it to 4, EF generates `WHERE Version = 3` and sets `Version = 4`. This works for single-update scenarios. BUT: if any code manually sets `Version` before save, or if Version is mapped from a DTO with a stale value, the optimistic concurrency is silently defeated because `SetTimestamps` always overwrites it. The `UpdatePoiAsync` method copies `poi.Version` from the incoming object (line 104 is missing -- it copies every field but Version, which means Version on the existing entity is whatever was loaded). This is fragile and relies on the implicit behavior of NOT copying Version.
**Impact:** Concurrency conflicts may not be detected if code paths evolve to copy Version from DTOs.
**Fix:** Either (a) explicitly document and enforce that Version must never be copied from external sources, or (b) use a `[Timestamp]` / `rowversion` column instead of manual increment, or (c) add a guard in `UpdatePoiAsync` that checks the incoming Version matches the existing Version before applying changes.

---

### [HIGH] UpdatePoiAsync Comment Lies -- It Does Not "Mark Only Changed Properties"

**File:** `Services/PoiService.cs` (lines 89-92, 103-121)
**Problem:** The XML doc says "marking only changed properties as modified, instead of overwriting all columns." This is a lie. The method unconditionally copies ALL properties from the incoming `Poi` to the existing entity. EF change tracking will detect which values actually changed, but the code itself overwrites everything. The comment is misleading about the implementation technique.
**Impact:** Misleading documentation causes maintainers to believe partial updates are happening when they are not. If any property should NOT be overwritten (e.g., AddedDate, Version), this pattern is dangerous.
**Fix:** Either implement actual partial updates (only copy non-default/explicitly-set properties) or fix the comment to say "loads existing entity and applies all incoming values, relying on EF change tracking to generate minimal SQL."

---

### [MEDIUM] Tags Stored as Comma-Separated String -- First Normal Form Violation

**File:** `Data/Entities/Poi.cs` (line 31)
**Problem:** Tags are stored as a comma-separated string in a single column. This makes querying by tag impossible without LIKE patterns (which cannot use indexes efficiently), makes adding/removing individual tags error-prone (string manipulation), and prevents referential integrity or tag normalization.
**Impact:** Slow tag-based queries; duplicate tag values with different casing/spacing; no way to enumerate all unique tags efficiently.
**Fix:** Create a separate `Tag` entity with a many-to-many relationship. If SQLite limitations preclude this, at minimum document the format contract and add helper methods for consistent parsing/serialization.

---

### [MEDIUM] No CreatePoiAsync or AddPoiToCollectionAsync Methods

**File:** `Services/IPoiService.cs`, `Services/PoiService.cs`
**Problem:** The service has Update, Delete, Search, Get, and GetCollection methods but NO method to create a new POI or add a POI to a collection. This means creation logic must live elsewhere (likely scattered across import services or UI code-behind), violating the Single Responsibility Principle. The service is an incomplete abstraction.
**Impact:** POI creation logic is duplicated/scattered; no single place to enforce creation validation; harder to test.
**Fix:** Add `CreatePoiAsync(Poi poi, int collectionId)` and `AddPoiToCollectionAsync(int poiId, int collectionId)` methods to the interface and implementation.

---

### [MEDIUM] GetVisiblePoisGroupedAsync Loads Full Poi Entities into Memory

**File:** `Services/PoiService.cs` (lines 56-67)
**Problem:** This method loads ALL POIs from ALL visible collections into memory at once via `.Include(ci => ci.Poi).ToListAsync()`. For a user with thousands of POIs across dozens of collections, this is a massive memory allocation. The data is then regrouped in-memory with LINQ GroupBy.
**Impact:** Memory pressure; slow initial page load; potential OOM on large datasets.
**Fix:** Consider server-side grouping, pagination, or loading only the fields needed for map markers (Id, Name, Lat, Lon, Address) using a projection/DTO.

---

### [MEDIUM] LeafletMapService is Not Thread-Safe

**File:** `Services/LeafletMapService.cs`
**Problem:** `_disposed` and `_dotnetRef` are accessed without synchronization. In Blazor Server, multiple async operations can interleave on the same circuit. If `DisposeAsync` runs concurrently with `InitMapAsync`, `_dotnetRef` could be disposed after the null check but before use, or `_disposed` could be read as false in `InvokeJsVoidAsync` while `DisposeAsync` is setting it to true.
**Impact:** Rare but possible `ObjectDisposedException` or use-after-dispose in Blazor Server scenarios.
**Fix:** Use `Interlocked.Exchange` for `_disposed` (as an int flag) or a `SemaphoreSlim` to synchronize access. For Blazor WASM (single-threaded), this is less critical but still a design smell.

---

### [MEDIUM] IMapService Leaks Implementation Details via Event

**File:** `Services/IMapService.cs` (line 15)
**Problem:** `event Action<int>? OnMarkerClicked` on an interface is a code smell. Events on interfaces create tight coupling between the service and its consumers, make testing harder (mocking events is awkward), and the `Action<int>` signature gives no context about what the `int` represents (POI ID? Marker index?). The event also has no way to unsubscribe safely with async disposal patterns.
**Impact:** Difficult to mock in tests; consumers must remember to unsubscribe; potential memory leaks from dangling event handlers.
**Fix:** Replace with a callback delegate set via a method (`void SetMarkerClickedCallback(Action<int> callback)`), or use an `IObservable<int>` / event aggregator pattern, or at minimum document the int parameter.

---

### [MEDIUM] DeleteCollectionAsync Silently Swallows "Not Found"

**File:** `Services/PoiService.cs` (lines 139-144)
**Problem:** When the collection is not found, the method logs a warning and returns silently. Other methods like `ToggleVisibilityAsync` and `UpdateCollectionColorAsync` throw `InvalidOperationException` for the same scenario. This inconsistency means callers cannot distinguish "successfully deleted" from "didn't exist."
**Impact:** Silent failures; inconsistent API contract; callers cannot reliably confirm deletion.
**Fix:** Either throw `InvalidOperationException` consistently (recommended) or return a `bool` indicating whether deletion occurred.

---

### [MEDIUM] Explicit Transaction in DeleteCollectionAsync May Be Unnecessary

**File:** `Services/PoiService.cs` (lines 132-174)
**Problem:** The method uses `BeginTransactionAsync` with manual commit/rollback. However, `SaveChangesAsync` already wraps its work in a transaction. The explicit transaction is needed here because there are TWO `SaveChangesAsync` calls (one to delete the collection, one to delete orphans). But the `catch` block calls `RollbackAsync` then rethrows -- EF Core would rollback the transaction automatically when the `DbContext` is disposed. The explicit rollback is redundant boilerplate.
**Impact:** Unnecessary complexity; the rollback in catch is dead code since disposal handles it.
**Fix:** Remove the try/catch/rollback and let disposal handle rollback. Keep the explicit transaction since there are two saves, but simplify to just `await transaction.CommitAsync()` after the second save, with no catch block.

---

### [MEDIUM] SearchAsync Uses ToLowerInvariant but SQLite LIKE is Case-Insensitive by Default

**File:** `Services/PoiService.cs` (lines 183-184)
**Problem:** The search converts the query to lowercase with `ToLowerInvariant()` then uses `EF.Functions.Like`. For SQLite (the likely DB given `CURRENT_TIMESTAMP` syntax), LIKE is case-insensitive for ASCII characters by default. The `ToLowerInvariant()` is therefore pointless for SQLite AND potentially harmful: if the DB is case-sensitive (e.g., PostgreSQL), comparing lowered query against non-lowered column data will miss matches. Either way, one side is wrong.
**Impact:** Unnecessary string allocation; false sense of case-insensitive search; will break if the DB engine changes.
**Fix:** For SQLite, remove the `ToLowerInvariant()` call. For portability, use `EF.Functions.Collate()` or `LOWER()` on both sides.

---

### [LOW] PoiCategory Has No IsValid Method Unlike PoiStatus

**File:** `Data/Entities/PoiStatus.cs` (lines 22-37)
**Problem:** `PoiStatus` has a convenient `IsValid(string?)` method. `PoiCategory` and `CollectionSourceType` do not, despite having the same `All` list pattern. Inconsistent API surface.
**Impact:** Callers must write their own validation logic for categories and source types; inconsistency invites bugs.
**Fix:** Add `IsValid(string?)` methods to `PoiCategory` and `CollectionSourceType` for parity.

---

### [LOW] PoiStatus.IsValid Treats null as Valid

**File:** `Data/Entities/PoiStatus.cs` (line 15-16)
**Problem:** `IsValid` returns `true` for `null`. Whether this is correct depends on business rules, but it means an entity with no status is considered "valid." If Status is meant to always have a value, this is a bug. If it's optional, this should be documented.
**Impact:** Potentially allows POIs with no status to bypass validation.
**Fix:** Document the business rule. If Status is required, change to `status is not null && All.Contains(status)`.

---

### [LOW] Duplicated MaxLength Declarations -- Attributes AND Fluent API

**File:** `Data/Entities/Poi.cs` (attributes), `Data/AppDbContext.cs` (lines 18-29)
**Problem:** Every `MaxLength` is declared twice: once as a `[MaxLength]` attribute on the entity property and again via Fluent API in `OnModelCreating`. This is pure redundancy. When someone changes one and forgets the other, they'll get a confusing mismatch.
**Impact:** Maintenance burden; risk of divergence; violates DRY.
**Fix:** Pick one approach and stick with it. Since the Fluent API is already used for indexes and check constraints, remove the `[MaxLength]` attributes from entities and use Fluent API exclusively. Or remove the Fluent API MaxLength calls since attributes are sufficient.

---

### [LOW] GeoUtils.EarthRadiusMeters Should Be const, Not static readonly

**File:** `Services/GeoUtils.cs` (line 6)
**Problem:** `EarthRadiusMeters` is declared `static readonly` but its value is a compile-time constant (`6371000`). It should be `const` for clarity and minor performance benefit (inlined by the compiler).
**Impact:** Negligible performance; stylistic inconsistency.
**Fix:** Change to `private const double EarthRadiusMeters = 6371000;`

---

### [LOW] No RemovePoiFromCollectionAsync Method

**File:** `Services/IPoiService.cs`
**Problem:** There is no method to remove a POI from a specific collection without deleting the entire collection. The only removal path is `DeleteCollectionAsync`, which nukes everything.
**Impact:** Users cannot curate individual POIs within a collection through the service layer.
**Fix:** Add `RemovePoiFromCollectionAsync(int poiId, int collectionId)` with orphan cleanup logic.

---

### [LOW] Anonymous Type DTO in ShowCollectionAsync

**File:** `Services/LeafletMapService.cs` (lines 29-37)
**Problem:** The method creates an anonymous type to pass POI data to JavaScript. This works but is fragile -- any property rename silently breaks the JS interop contract. There's no compile-time contract between the C# anonymous type and the JS function signature.
**Impact:** Silent breakage if properties are renamed; no reuse of the DTO shape; harder to unit test.
**Fix:** Create a named `MarkerDto` record/class that documents the JS interop contract.

---

### [LOW] Missing GC.SuppressFinalize in DisposeAsync

**File:** `Services/LeafletMapService.cs` (line 74)
**Problem:** `DisposeAsync` does not call `GC.SuppressFinalize(this)`. While the class has no finalizer, the `IAsyncDisposable` pattern recommends it to prevent the finalizer from running if one is added later or by a derived class.
**Impact:** Minor; no current finalizer, but violates the recommended disposal pattern.
**Fix:** Add `GC.SuppressFinalize(this);` at the start of `DisposeAsync`.

---

### [NITPICK] Inconsistent Error Handling Philosophy

**File:** `Services/PoiService.cs`
**Problem:** `ToggleVisibilityAsync`, `UpdatePoiAsync`, and `UpdateCollectionColorAsync` throw on not-found. `DeleteCollectionAsync` silently returns. `GetPoiAsync` returns null. Three different "not found" strategies in one service.
**Impact:** Callers must remember which method does what; inconsistency invites bugs.
**Fix:** Standardize on one approach. Recommended: throw for mutations, return null/empty for queries.

---

### [NITPICK] PoiCollectionItem Has a Surrogate Id That May Be Unnecessary

**File:** `Data/Entities/PoiCollectionItem.cs` (line 5)
**Problem:** The join entity has its own `Id` primary key, but there is already a unique index on `(PoiId, PoiCollectionId)`. A composite primary key would eliminate the surrogate key and the separate unique index, reducing storage and index overhead.
**Impact:** Slightly more storage; one extra index to maintain; negligible in practice.
**Fix:** Consider using `(PoiId, PoiCollectionId)` as the composite primary key. EF Core supports this via `HasKey(e => new { e.PoiId, e.PoiCollectionId })`.

---

### [NITPICK] Missing `required` Keyword on Non-Nullable String Properties

**File:** `Data/Entities/Poi.cs` (line 10), `Data/Entities/PoiCollection.cs` (line 10)
**Problem:** `Name` is initialized to `string.Empty` to avoid nullable warnings, but this means a default-constructed `Poi` has an empty name that will pass null checks. C# 11's `required` keyword would force callers to explicitly set Name during construction.
**Impact:** Allows accidental creation of POIs/collections with empty names.
**Fix:** Add `required` modifier: `public required string Name { get; set; }` (requires C# 11+).

---

### [NITPICK] PoiStatus.cs File Contains Three Unrelated Classes

**File:** `Data/Entities/PoiStatus.cs`
**Problem:** `PoiStatus`, `PoiCategory`, and `CollectionSourceType` are all in the same file. The filename `PoiStatus.cs` does not reflect this. Violates one-class-per-file convention.
**Impact:** Harder to find classes; git blame/history is muddied.
**Fix:** Split into `PoiStatus.cs`, `PoiCategory.cs`, and `CollectionSourceType.cs`.

---

### [NITPICK] Return Type List<T> Instead of IReadOnlyList<T> in Service Interface

**File:** `Services/IPoiService.cs`
**Problem:** All query methods return `List<T>`, exposing the mutable collection to callers. Callers can `Add`/`Remove` items from the returned list, potentially causing confusion about whether changes affect the DB.
**Impact:** Minor; allows unintended mutation of result sets.
**Fix:** Return `IReadOnlyList<T>` from service query methods.

---

## Summary Table

| # | Severity | Title | File(s) |
|---|----------|-------|---------|
| 1 | CRITICAL | LIKE wildcard injection in SearchAsync | PoiService.cs |
| 2 | CRITICAL | Denormalized PoiCount consistency timebomb | PoiCollection.cs, PoiService.cs |
| 3 | HIGH | No validation of Status/Category before persistence | PoiService.cs, Poi.cs |
| 4 | HIGH | No coordinate/name validation in UpdatePoiAsync | PoiService.cs |
| 5 | HIGH | Color MaxLength(9) vs regex allowing only 7 chars | PoiCollection.cs, PoiService.cs |
| 6 | HIGH | Version increment conflicts with ConcurrencyCheck semantics | AppDbContext.cs |
| 7 | HIGH | UpdatePoiAsync comment lies about partial updates | PoiService.cs |
| 8 | MEDIUM | Tags as comma-separated string (1NF violation) | Poi.cs |
| 9 | MEDIUM | No Create/Add methods -- incomplete service abstraction | IPoiService.cs, PoiService.cs |
| 10 | MEDIUM | GetVisiblePoisGroupedAsync loads all POIs into memory | PoiService.cs |
| 11 | MEDIUM | LeafletMapService not thread-safe | LeafletMapService.cs |
| 12 | MEDIUM | IMapService leaks implementation via event | IMapService.cs |
| 13 | MEDIUM | DeleteCollectionAsync silently swallows not-found | PoiService.cs |
| 14 | MEDIUM | Explicit transaction rollback is redundant | PoiService.cs |
| 15 | MEDIUM | ToLowerInvariant is pointless for SQLite LIKE | PoiService.cs |
| 16 | LOW | PoiCategory/CollectionSourceType missing IsValid | PoiStatus.cs |
| 17 | LOW | PoiStatus.IsValid treats null as valid (undocumented) | PoiStatus.cs |
| 18 | LOW | Duplicated MaxLength in attributes AND Fluent API | Poi.cs, AppDbContext.cs |
| 19 | LOW | EarthRadiusMeters should be const not static readonly | GeoUtils.cs |
| 20 | LOW | No RemovePoiFromCollectionAsync method | IPoiService.cs |
| 21 | LOW | Anonymous type DTO in ShowCollectionAsync | LeafletMapService.cs |
| 22 | LOW | Missing GC.SuppressFinalize in DisposeAsync | LeafletMapService.cs |
| 23 | NITPICK | Inconsistent error handling philosophy across methods | PoiService.cs |
| 24 | NITPICK | Surrogate Id on join table may be unnecessary | PoiCollectionItem.cs |
| 25 | NITPICK | Missing `required` keyword on Name properties | Poi.cs, PoiCollection.cs |
| 26 | NITPICK | Three classes in one file named PoiStatus.cs | PoiStatus.cs |
| 27 | NITPICK | Returning mutable List<T> instead of IReadOnlyList<T> | IPoiService.cs |

---

## Verdict

**Findings:** 2 CRITICAL, 5 HIGH, 7 MEDIUM, 6 LOW, 5 NITPICK -- **27 total issues**

**Elegance Score: 4/10**

The bones are decent -- proper use of `IDbContextFactory`, cancellation tokens throughout, sensible disposal patterns in LeafletMapService, and check constraints in the DB. But the service layer is riddled with missing validation, the denormalized PoiCount is an accident waiting to happen, and the inconsistencies (error handling, MaxLength duplication, color format mismatches) reveal a codebase that was assembled incrementally without a governing design standard. The LIKE injection and absent input validation in mutation methods are the kind of issues that make a principal engineer question whether code review is even happening. Fix the criticals and highs before this goes anywhere near production.
