# LucidCartographer Data Layer & Core Services Review

Reviewed: 2026-04-12
Reviewer: Principal Engineer (grumpy, thorough)
Scope: Entities, DbContext, PoiService, GeoUtils, IMapService, LeafletMapService

---

## ENTITIES

### [CRITICAL] No validation constraints on Latitude/Longitude
- **File:** `Data/Entities/Poi.cs:7-8`
- **Problem:** `Latitude` and `Longitude` are bare `double` properties with no range constraints. Latitude must be [-90, 90], longitude [-180, 180]. Neither the entity nor the `OnModelCreating` configuration enforces this. A POI at latitude 9999 will happily persist.
- **Impact:** Corrupt data silently enters the database. Map rendering breaks, Haversine calculations produce garbage, and you get to debug it in production at 2 AM.
- **Fix:** Add a value validation attribute or fluent API check constraint. At minimum: `.HasCheckConstraint("CK_Poi_Latitude", "Latitude >= -90 AND Latitude <= 90")` and equivalent for Longitude. Also add `[Range(-90, 90)]` / `[Range(-180, 180)]` for API-level validation.

### [CRITICAL] `Rating` has no range constraint
- **File:** `Data/Entities/Poi.cs:15`
- **Problem:** Comment says "personal 1-5" but nothing enforces it. You can store Rating = -42 or Rating = 9001.
- **Impact:** UI code that assumes 1-5 range will display nonsense or crash. Data integrity is a joke.
- **Fix:** Add a check constraint: `.HasCheckConstraint("CK_Poi_Rating", "Rating IS NULL OR (Rating >= 1 AND Rating <= 5)")`. Add `[Range(1, 5)]` attribute.

### [CRITICAL] `Status` and `Category` are magic strings instead of enums
- **File:** `Data/Entities/Poi.cs:11-12`
- **Problem:** `Status` is a `string?` with a comment listing valid values ("visited, want_to_go, imported"). `Category` is also a free-form string. No enum, no check constraint, no validation. One typo ("visted") and your filtering silently returns nothing.
- **Impact:** Impossible to refactor safely, no compile-time checking, data quality degrades over time, queries on Status are fragile.
- **Fix:** Create `PoiStatus` and `PoiCategory` enums. Store as string via `.HasConversion<string>()` if you want human-readable DB values, or store as int for performance.

### [WARNING] `Tags` stored as comma-separated string
- **File:** `Data/Entities/Poi.cs:13`
- **Problem:** Tags are a comma-separated string. This is a well-known anti-pattern. You cannot efficiently query "all POIs with tag X" without `LIKE '%X%'` which is slow and incorrect (searching for tag "bar" matches "foobar"). No normalization.
- **Impact:** Cannot index tags, cannot do accurate tag filtering, cannot enforce tag uniqueness or referential integrity.
- **Fix:** Create a `PoiTag` entity with a many-to-many relationship, or at minimum use a JSON column with EF Core's JSON support if on a recent provider.

### [WARNING] `PoiCount` on `PoiCollection` is a denormalized counter that will drift
- **File:** `Data/Entities/PoiCollection.cs:14`
- **Problem:** `PoiCount` is a manually maintained integer. Nothing in the codebase appears to keep it in sync when POIs are added/removed. The `DeleteCollectionAsync` method doesn't touch it, and no trigger or computed column exists.
- **Impact:** The count will diverge from reality. UI shows wrong numbers. Users lose trust.
- **Fix:** Either (a) remove it and compute count on the fly with `.Count()`, (b) make it a computed column/view, or (c) rigorously maintain it in every mutation path with a helper method. Option (a) is simplest and correct.

### [WARNING] `SourceType` on `PoiCollection` is another magic string
- **File:** `Data/Entities/PoiCollection.cs:12`
- **Problem:** Same issue as `Poi.Status`. Comment says "gpx_import, kml_import, manual, operation_result" but it's a raw string.
- **Impact:** Same as Status -- no compile-time safety, prone to typos, hard to refactor.
- **Fix:** Create a `CollectionSourceType` enum.

### [WARNING] `DateTime.UtcNow` as default value in entity constructors
- **File:** `Data/Entities/Poi.cs:23`, `Data/Entities/PoiCollection.cs:11`
- **Problem:** `AddedDate` and `CreatedDate` default to `DateTime.UtcNow` at object construction time, not at database insert time. If you create an entity, hold it in memory for a while, then save -- the timestamp is wrong. Also makes testing impossible without hacks because you can't control the clock.
- **Impact:** Inaccurate timestamps, untestable code.
- **Fix:** Set timestamps in the `DbContext.SaveChanges` override or use `HasDefaultValueSql("CURRENT_TIMESTAMP")` in fluent config. Inject an `ITimeProvider`/`TimeProvider` (.NET 8+) for testability.

### [WARNING] No `MaxLength` constraints on any string property
- **File:** `Data/Entities/Poi.cs` (all string properties), `Data/Entities/PoiCollection.cs` (all string properties)
- **Problem:** `Name`, `Address`, `GoogleMapsUrl`, `Website`, `Phone`, `Notes`, `Tags`, `Color`, etc. all have no `MaxLength`. EF Core will create `nvarchar(max)` columns (SQL Server) or `TEXT` (SQLite). The `GoogleMapsUrl` index on an unbounded column is either inefficient or will fail on some providers.
- **Impact:** Wasted storage, potential index failures, no protection against someone stuffing 10MB into the `Notes` field.
- **Fix:** Add `[MaxLength(N)]` or `.HasMaxLength(N)` for every string column. `Name` -> 500, `GoogleMapsUrl` -> 2048, `Phone` -> 50, `Color` -> 9, etc.

### [MINOR] `Color` has no format validation
- **File:** `Data/Entities/PoiCollection.cs:8`
- **Problem:** `Color` defaults to `"#005bbf"` which implies hex format, but nothing enforces it. Someone could store "banana" as a color.
- **Impact:** JS/CSS rendering will fail silently or produce invisible markers.
- **Fix:** Add a regex validation attribute or a check constraint for hex color format.

### [MINOR] `GoogleRating` has no range constraint
- **File:** `Data/Entities/Poi.cs:16`
- **Problem:** Google ratings are 1.0-5.0 but nothing enforces this.
- **Impact:** Minor data integrity concern.
- **Fix:** Add check constraint `GoogleRating IS NULL OR (GoogleRating >= 1.0 AND GoogleRating <= 5.0)`.

### [NITPICK] `ReviewCount` should be unsigned / non-negative
- **File:** `Data/Entities/Poi.cs:17`
- **Problem:** A review count of -5 makes no sense.
- **Impact:** Minor data integrity.
- **Fix:** Add check constraint `ReviewCount IS NULL OR ReviewCount >= 0`.

---

## DB CONTEXT

### [WARNING] No cascade delete strategy for orphan POIs is configured at the DB level
- **File:** `Data/AppDbContext.cs:36,40`
- **Problem:** Both FK relationships use `DeleteBehavior.Cascade`, which deletes `PoiCollectionItem` rows when a `Poi` or `PoiCollection` is deleted. But the orphaned-POI cleanup is done manually in `PoiService.DeleteCollectionAsync` as a second query after the main delete. If the app crashes between the two `SaveChangesAsync` calls, orphan POIs remain forever.
- **Impact:** Data leak -- orphaned POIs accumulate silently.
- **Fix:** Wrap both operations in a single transaction, or better yet, use a database trigger or a stored procedure. At minimum: `using var tx = await db.Database.BeginTransactionAsync();`.

### [MINOR] Index on `GoogleMapsUrl` without MaxLength
- **File:** `Data/AppDbContext.cs:18`
- **Problem:** Indexing a potentially unbounded string column is provider-dependent. On SQL Server, indexes on `nvarchar(max)` are not supported. On SQLite it works but is inefficient.
- **Impact:** Migration may fail on certain providers, or the index is silently ignored.
- **Fix:** Add `HasMaxLength(2048)` to `GoogleMapsUrl` before indexing it.

### [NITPICK] No `OnDelete` behavior explicitly set for the index-only configurations
- **File:** `Data/AppDbContext.cs:16-21`
- **Problem:** The Poi entity configuration only sets up indexes. That's fine, but there's no explicit column type configuration for any property. The entire schema relies on convention.
- **Impact:** Low risk but means you're one convention change away from a broken migration.
- **Fix:** Be explicit about key column types and string lengths in fluent configuration.

---

## POI SERVICE

### [CRITICAL] N+1 query in `GetVisiblePoisGroupedAsync`
- **File:** `Services/PoiService.cs:31-49`
- **Problem:** This method first queries all visible collection IDs, then loops over each one and issues a separate query per collection. If you have 50 visible collections, that's 51 database round-trips.
- **Impact:** Performance degrades linearly with collection count. This is called on every map load. Expect sluggish UI with non-trivial data.
- **Fix:** Single query with a join:
  ```csharp
  var items = await db.PoiCollectionItems
      .Where(ci => ci.PoiCollection.IsVisible)
      .Include(ci => ci.Poi)
      .ToListAsync();
  return items.GroupBy(ci => ci.PoiCollectionId)
      .ToDictionary(g => g.Key, g => g.Select(ci => ci.Poi).ToList());
  ```

### [CRITICAL] `UpdatePoiAsync` uses `Update()` on a detached entity -- full overwrite risk
- **File:** `Services/PoiService.cs:68-73`
- **Problem:** The `Poi` parameter comes from outside the DbContext scope (detached entity). Calling `db.Pois.Update(poi)` marks ALL properties as modified, meaning every column gets overwritten, even ones the caller didn't intend to change. If the caller has stale data for some fields, those stale values silently overwrite the current DB values.
- **Impact:** Silent data corruption. Race conditions between concurrent Blazor Server users will cause last-write-wins on ALL columns, not just the ones actually modified.
- **Fix:** Fetch the existing entity, map only the changed properties, then save. Or use `Attach` + explicitly mark individual properties as modified.

### [CRITICAL] Orphan cleanup in `DeleteCollectionAsync` is not transactional
- **File:** `Services/PoiService.cs:75-93`
- **Problem:** Two separate `SaveChangesAsync()` calls (line 84 and line 91) with no explicit transaction. If the process crashes after the first save but before the second, orphaned POIs remain. Also, after the first `SaveChangesAsync` removes the collection and its items, the second query for orphans runs against the same context -- but the cascade-deleted items are gone from the tracker, so the orphan query should work. However, there's still the crash window.
- **Impact:** Orphaned data accumulates over time. No way to detect or clean up without a maintenance script.
- **Fix:** Wrap in `await using var tx = await db.Database.BeginTransactionAsync();` ... `await tx.CommitAsync();`.

### [WARNING] `PoiService` has no interface
- **File:** `Services/PoiService.cs:7`
- **Problem:** `PoiService` is a concrete class with no `IPoiService` interface. Every consumer depends directly on the implementation. This violates DIP (Dependency Inversion Principle) and makes unit testing components that use this service impossible without a real database.
- **Impact:** Cannot mock for testing. Cannot swap implementations. Tight coupling throughout the app.
- **Fix:** Extract `IPoiService` interface. Register as `services.AddScoped<IPoiService, PoiService>()`.

### [WARNING] `SearchAsync` does not validate input
- **File:** `Services/PoiService.cs:95-106`
- **Problem:** No null/empty check on `query`. Passing `null` will throw `NullReferenceException` on `query.ToLowerInvariant()`. Passing empty string returns everything (up to 100 rows) which may not be intended.
- **Impact:** Runtime crash on null input. Unexpected bulk data return on empty string.
- **Fix:** Guard clause: `if (string.IsNullOrWhiteSpace(query)) return new List<Poi>();`. Or throw `ArgumentException`.

### [WARNING] `SearchAsync` uses `ToLower()` inside LINQ -- provider-dependent behavior
- **File:** `Services/PoiService.cs:100-103`
- **Problem:** `p.Name.ToLower().Contains(lower)` may not translate well to all EF Core providers. SQLite is case-insensitive by default for ASCII. SQL Server has collation-based behavior. The `ToLower()` call may cause a full table scan by preventing index usage.
- **Impact:** Poor search performance on larger datasets. Provider-portability issue.
- **Fix:** Use `EF.Functions.Like()` or configure case-insensitive collation at the column level. For SQLite, just use `Contains()` without `ToLower()`.

### [WARNING] `UpdateCollectionColorAsync` does not validate the color value
- **File:** `Services/PoiService.cs:108-117`
- **Problem:** Accepts any string as a color. No hex format validation. Combined with the entity having no constraint, this is a data quality hole.
- **Impact:** Invalid colors persist and break the UI.
- **Fix:** Validate hex color format before saving: `if (!Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$")) throw new ArgumentException(...)`.

### [WARNING] `ToggleVisibilityAsync` silently does nothing on invalid ID
- **File:** `Services/PoiService.cs:51-59`
- **Problem:** If `collectionId` doesn't exist, `FindAsync` returns null, and the method silently returns. The caller has no idea the operation was a no-op.
- **Impact:** UI appears to work but nothing happened. Debugging this is maddening.
- **Fix:** Throw an exception or return a bool indicating success. `throw new InvalidOperationException($"Collection {collectionId} not found")` or return `Task<bool>`.

### [MINOR] `GetPoisByCollectionAsync` does not verify collection exists
- **File:** `Services/PoiService.cs:22-29`
- **Problem:** If the collection ID doesn't exist, this returns an empty list. The caller can't distinguish "collection has no POIs" from "collection doesn't exist."
- **Impact:** Silent failures, confusing UX.
- **Fix:** Consider returning `null` for non-existent collection or throwing, and an empty list only for existing-but-empty collections.

### [MINOR] No cancellation token support anywhere
- **File:** `Services/PoiService.cs` (all methods)
- **Problem:** None of the async methods accept a `CancellationToken`. In Blazor Server, if a user navigates away, the circuit may be disposed but the queries keep running.
- **Impact:** Wasted server resources on abandoned operations. Potential `ObjectDisposedException` when the result tries to update a disposed component.
- **Fix:** Add `CancellationToken cancellationToken = default` to all async methods and pass it through to EF Core calls.

---

## GEO UTILS

### [MINOR] `HaversineDistance` does not validate inputs
- **File:** `Services/GeoUtils.cs:7`
- **Problem:** No validation that lat/lon are within valid ranges. `NaN` or `Infinity` inputs produce `NaN` output silently.
- **Impact:** Garbage in, garbage out -- silently.
- **Fix:** Add guard clauses or at least document the expected ranges. Consider `ArgumentOutOfRangeException` for invalid coordinates.

### [NITPICK] `DegreesToRadians` could use `double.DegreesToRadians` (.NET 8+)
- **File:** `Services/GeoUtils.cs:18`
- **Problem:** Manual conversion when the framework provides it (if targeting .NET 8+).
- **Impact:** Negligible, but why maintain code the framework gives you for free?
- **Fix:** Use `double.DegreesToRadians(degrees)` if on .NET 8+.

---

## MAP SERVICE INTERFACE & IMPLEMENTATION

### [WARNING] `IMapService` leaks entity types into the presentation contract
- **File:** `Services/IMapService.cs:8`
- **Problem:** `ShowCollectionAsync` takes `List<Poi>` -- a data entity. The map service interface now depends on the data layer. Any change to the `Poi` entity ripples into the map service contract and all its consumers.
- **Impact:** Violates ISP and DIP. Tight coupling between presentation and data layer. Cannot use this interface without referencing the entity assembly.
- **Fix:** Define a `MapMarkerDto` or similar lightweight DTO. The interface should accept `IReadOnlyList<MapMarkerDto>`, not entity objects.

### [WARNING] `LeafletMapService` is not thread-safe for Blazor Server
- **File:** `Services/LeafletMapService.cs:10-11`
- **Problem:** `OnMarkerClicked` is a plain `event Action<int>?`. In Blazor Server, multiple components could subscribe/unsubscribe from different threads. The `event` keyword in C# uses a lock-free pattern that is not thread-safe for concurrent add/remove. Also, `_dotnetRef` is set in `InitMapAsync` with no synchronization.
- **Impact:** Potential race conditions, lost event subscriptions, or `NullReferenceException` on the event invocation.
- **Fix:** Use `Interlocked` patterns or a thread-safe event implementation. Consider using a dedicated callback delegate set once at construction.

### [WARNING] `LeafletMapService.DisposeAsync` doesn't clean up the JS-side map
- **File:** `Services/LeafletMapService.cs:70-73`
- **Problem:** `DisposeAsync` only disposes the `_dotnetRef`. It never calls a JS cleanup function to destroy the Leaflet map instance, remove event listeners, or free DOM resources.
- **Impact:** Memory leaks in the browser. Every navigation that creates a new map instance leaks the old one. Over time, the tab consumes unbounded memory.
- **Fix:** Call `await _js.InvokeVoidAsync("leafletInterop.destroyMap")` before disposing the .NET ref. Wrap in try-catch for the case where the circuit is already disconnected.

### [WARNING] No JSDisconnectedException handling in `LeafletMapService`
- **File:** `Services/LeafletMapService.cs` (all async methods)
- **Problem:** In Blazor Server, if the SignalR circuit is disconnected, any `IJSRuntime.InvokeVoidAsync` call throws `JSDisconnectedException`. None of the methods handle this.
- **Impact:** Unhandled exceptions crash the component when the user's connection is flaky or they navigate away.
- **Fix:** Wrap JS interop calls in try-catch for `JSDisconnectedException` (and `ObjectDisposedException`). Consider a helper method.

### [WARNING] `ShowCollectionAsync` creates anonymous objects for JS interop
- **File:** `Services/LeafletMapService.cs:26-34`
- **Problem:** Anonymous types with lowercase property names are used for JS serialization. This works by accident because `System.Text.Json` in Blazor uses camelCase by default. But it's implicit and fragile -- if serialization settings change, or if someone adds `[JsonPropertyName]` attributes elsewhere, this breaks silently.
- **Impact:** Fragile serialization, no compile-time contract.
- **Fix:** Create a proper `MapMarkerDto` record/class with explicit `[JsonPropertyName]` attributes or use the DTO from the interface fix above.

### [MINOR] `OnMarkerClickedJs` is public but is only meant for JS interop
- **File:** `Services/LeafletMapService.cs:63-68`
- **Problem:** The `[JSInvokable]` method is `public`, which means any C# code can call it directly, bypassing the JS callback flow. It's an implementation detail leaking into the public API.
- **Impact:** Confusing API surface. Someone might call it directly thinking it's a normal method.
- **Fix:** Unfortunately `[JSInvokable]` requires `public`. Document it with `/// <summary>Internal: called from JavaScript only.</summary>` and consider a naming convention like `__OnMarkerClickedJs` (though ugly, it signals "don't touch").

### [MINOR] `IMapService` event uses `Action<int>` -- no async support
- **File:** `Services/IMapService.cs:14`
- **Problem:** `event Action<int>? OnMarkerClicked` -- subscribers cannot do async work (like loading POI details from the database) without `async void`, which swallows exceptions.
- **Impact:** Forces `async void` usage in subscribers, which is a known anti-pattern that loses exception context.
- **Fix:** Use `Func<int, Task>?` as a callback property instead of an event, or use a custom async event pattern.

### [MINOR] `InitMapAsync` can be called multiple times, leaking `_dotnetRef`
- **File:** `Services/LeafletMapService.cs:18-22`
- **Problem:** If `InitMapAsync` is called twice, the old `_dotnetRef` is overwritten without being disposed.
- **Impact:** GC handle leak. The old `DotNetObjectReference` is never freed.
- **Fix:** Dispose the existing `_dotnetRef` before creating a new one, or throw if already initialized.

---

## CROSS-CUTTING CONCERNS

### [CRITICAL] No concurrency control anywhere
- **File:** `Services/PoiService.cs` (all mutation methods)
- **Problem:** No optimistic concurrency tokens (`[ConcurrencyCheck]` or `[Timestamp]`/`rowversion`) on any entity. In Blazor Server, multiple users can edit the same POI simultaneously. The last save wins silently, overwriting the other user's changes.
- **Impact:** Data loss in multi-user scenarios. This is a Blazor Server app -- concurrent access is the norm, not the exception.
- **Fix:** Add a `[Timestamp] public byte[] RowVersion { get; set; }` to `Poi` and `PoiCollection`. Handle `DbUpdateConcurrencyException` in the service layer.

### [WARNING] No logging anywhere in the service layer
- **File:** `Services/PoiService.cs`, `Services/LeafletMapService.cs`
- **Problem:** Zero `ILogger` usage. No logging of operations, errors, or warnings.
- **Impact:** When something goes wrong in production, you have no telemetry. Good luck debugging the orphan POI issue or the silent toggle failure.
- **Fix:** Inject `ILogger<PoiService>` and `ILogger<LeafletMapService>`. Log at appropriate levels.

### [WARNING] No exception handling strategy
- **File:** `Services/PoiService.cs`, `Services/LeafletMapService.cs`
- **Problem:** No try-catch blocks, no custom exceptions, no error handling. Database failures, network issues, and invalid states all bubble up as raw infrastructure exceptions.
- **Impact:** Poor error messages for users. No ability to distinguish between "POI not found" and "database is down."
- **Fix:** Define domain exceptions (e.g., `PoiNotFoundException`). Catch infrastructure exceptions and wrap or log them.

### [MINOR] `GeoUtils` is never referenced by any service in this review scope
- **File:** `Services/GeoUtils.cs`
- **Problem:** `GeoUtils.HaversineDistance` is a public static method but none of the reviewed services use it. It may be used by components or other code, but it's worth verifying.
- **Impact:** Possibly dead code.
- **Fix:** Verify usage. If unused, remove. If used only by components, consider whether it belongs in a `Utilities` namespace instead of `Services`.

---

## SUMMARY

| Severity | Count |
|----------|-------|
| CRITICAL | 6     |
| WARNING  | 15    |
| MINOR    | 7     |
| NITPICK  | 3     |
| **Total** | **31** |

The top 3 things to fix immediately:
1. **N+1 query in `GetVisiblePoisGroupedAsync`** -- this is your main data loading path and it's O(N) database calls.
2. **`UpdatePoiAsync` full-overwrite on detached entity** -- silent data corruption waiting to happen.
3. **No concurrency control** -- in a Blazor Server app with shared state, this is a ticking time bomb.
