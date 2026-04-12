# Code Review: Operations & Matching Services

**Reviewer:** Principal Engineer
**Date:** 2026-04-12
**Scope:** `Services/Operations/PoiMatcher.cs`, `Services/Operations/SetOperationService.cs`, with context from `Services/GeoUtils.cs`, `Data/Entities/Poi.cs`, `Data/AppDbContext.cs`
**Verdict:** Reject. Multiple algorithmic correctness issues, O(n^2) performance traps baked into every public method, missing abstractions, and a `CommitResultAsync` that issues one DB query per POI in a loop. This code will fall over the moment someone feeds it a real-world dataset.

---

## CRITICAL

### OPS-C01: `CommitResultAsync` issues N+1 queries in a loop
**File:** `SetOperationService.cs:116-123`
**Severity:** Critical (Performance)

```csharp
foreach (var poi in pois)
{
    var exists = await db.PoiCollectionItems
        .AnyAsync(ci => ci.PoiId == poi.Id && ci.PoiCollectionId == collection.Id);
    if (!exists)
    {
        db.PoiCollectionItems.Add(new PoiCollectionItem { ... });
    }
}
```

For every single POI, this fires a separate `SELECT ... WHERE PoiId = @p AND PoiCollectionId = @p` query. For a collection of 5,000 POIs, that is 5,000 round-trips to the database. The existence check is also entirely pointless: the collection was **just created** on line 113, so `collection.Id` is brand new. No items can possibly exist yet. The entire `AnyAsync` call is dead logic performing work that will always return `false`.

**Fix:** Remove the existence check. Batch-add all items. Better yet, use `AddRange`:
```csharp
db.PoiCollectionItems.AddRange(pois.Select(p => new PoiCollectionItem
{
    PoiId = p.Id,
    PoiCollectionId = collection.Id
}));
await db.SaveChangesAsync();
```

---

### OPS-C02: Every set operation is O(n * m) with no short-circuit
**File:** `SetOperationService.cs:53-63`, `PoiMatcher.cs:33-35`
**Severity:** Critical (Performance)

`Subtract`, `Intersect`, and `Union` all call `FindMatch(poi, entireOtherCollection)` for every POI in collection A. `FindMatch` does a linear scan via `FirstOrDefault`. For two collections of size N and M, that is O(N * M) match comparisons. Each comparison itself calls `HaversineDistance` (transcendental math) and potentially `LevenshteinDistance` (O(len_a * len_b) with a 2D array allocation). For 10k x 10k, that is 100 million Haversine calls and up to 100 million Levenshtein matrix allocations.

**Fix:** Build a spatial index (e.g., grid-based bucketing by lat/lon cells, or an R-tree) to prune candidates before computing exact Haversine distance. Pre-index URLs into a `HashSet<string>` for O(1) Tier 1 lookups. The URL index alone would eliminate most of the expensive Tier 2 work.

---

### OPS-C03: `FindDuplicateGroups` is O(n^2) and produces non-transitive groups
**File:** `PoiMatcher.cs:41-70`
**Severity:** Critical (Correctness + Performance)

Two problems:

1. **O(n^2) brute force:** Nested loop over all POI pairs. For 10,000 POIs, that is 50 million pair checks.

2. **Non-transitive grouping:** The algorithm only matches each POI against the group's "anchor" (index `i`). If POI_A matches POI_B and POI_B matches POI_C, but POI_A does NOT match POI_C, then POI_C gets grouped with POI_A anyway (if POI_B was the anchor). Conversely, if POI_A is the anchor and POI_C does not match POI_A directly, POI_C is missed even though it is a transitive duplicate through POI_B. This is a union-find problem and needs a proper union-find data structure.

---

### OPS-C04: Levenshtein allocates a full 2D matrix every call
**File:** `PoiMatcher.cs:90-110`
**Severity:** Critical (Performance)

`new int[n + 1, m + 1]` allocates a 2D array on every invocation. For two 50-character names, that is a 51x51 = 2,601-element array. Since this is called inside an O(n*m) loop (OPS-C02), millions of these arrays are allocated and immediately become garbage. The GC pressure alone will crater throughput.

**Fix:** Use the classic two-row optimization (`int[] prev, int[] curr`) which reduces space from O(n*m) to O(min(n,m)) and eliminates the 2D array allocation. Or keep a thread-local/pooled buffer.

---

## HIGH

### OPS-H01: No `CancellationToken` support anywhere
**File:** `SetOperationService.cs` (all async methods), `PoiMatcher.cs`
**Severity:** High

`ExecuteAsync`, `CommitResultAsync`, `GetCollectionPois` -- none accept a `CancellationToken`. Given that the set operations can run for minutes on large datasets (see OPS-C02), the user has no way to cancel a long-running operation. The EF Core methods (`ToListAsync`, `AnyAsync`, `SaveChangesAsync`) all have `CancellationToken` overloads that are simply not being used.

---

### OPS-H02: `PoiMatcher` has no interface -- untestable, not injectable
**File:** `PoiMatcher.cs`
**Severity:** High (SOLID -- Dependency Inversion)

`PoiMatcher` is a concrete class with no `IPoiMatcher` interface. `SetOperationService` depends directly on the concrete type. This means:
- Unit testing `SetOperationService` requires a real `PoiMatcher` (no mocking).
- Swapping matching strategies (e.g., phonetic matching, ML-based matching) requires modifying `SetOperationService`.
- The DI container registers a concrete type.

Same applies to `SetOperationService` itself -- no `ISetOperationService`.

---

### OPS-H03: `NormalizeUrl` is naive -- fails on Google Maps URL variants
**File:** `PoiMatcher.cs:112-118`
**Severity:** High (Correctness)

Google Maps URLs come in many forms:
- `https://maps.google.com/maps?q=...`
- `https://www.google.com/maps/place/...`
- `https://goo.gl/maps/...` (short links)
- `https://maps.app.goo.gl/...` (newer short links)
- URLs with different query parameter ordering (`?q=foo&hl=en` vs `?hl=en&q=foo`)
- URLs with/without `www.`
- URLs with fragment identifiers (`#...`)
- URLs with tracking parameters (`utm_source`, etc.)

The current normalization only strips trailing slashes and lowercases. Two URLs pointing to the exact same place will fail to match if one uses `www.` and the other does not, or if query parameters are in different order, or if one is a short link. The `ToLowerInvariant()` call also corrupts case-sensitive path segments (place IDs in Google Maps URLs are case-sensitive).

---

### OPS-H04: `GetCollectionPois` may return null navigation properties
**File:** `SetOperationService.cs:134-140`
**Severity:** High (Correctness)

```csharp
.Select(ci => ci.Poi)
```

If `ci.Poi` navigation property is not loaded (lazy loading disabled, no explicit include), this could return a list of `null` entries depending on EF Core configuration. Even if the query translates to a JOIN correctly, there is no null check on the result. If a `PoiCollectionItem` references a deleted POI (orphaned FK), `ci.Poi` is null and downstream code (`IsMatch`, `HaversineDistance`) will throw `NullReferenceException`.

---

### OPS-H05: `IsMatch` falls through to Tier 2 even when URLs match a *different* place
**File:** `PoiMatcher.cs:14-19`
**Severity:** High (Correctness)

If both POIs have Google Maps URLs and the URLs do NOT match, the code falls through to Tier 2 (proximity + name check). This means two POIs with *different* Google Maps URLs -- explicitly identifying them as *different* places -- can still be matched if they happen to be close together and have similar names. This is semantically wrong: if you have authoritative URL identifiers that disagree, that should be a definitive non-match.

**Fix:** If both POIs have URLs and the normalized URLs differ, return `false` immediately.

---

### OPS-H06: Unicode normalization missing in `NameSimilarity`
**File:** `PoiMatcher.cs:72-88`
**Severity:** High (Correctness)

`ToLowerInvariant()` does not normalize Unicode. "Cafe\u0301" (e + combining acute) and "Caf\u00e9" (precomposed e-acute) are semantically identical but will not match. For a maps application that handles international POI names (accented French, German umlauts, Japanese, etc.), this is a real-world failure mode. The Levenshtein comparison will also penalize combining characters differently.

**Fix:** Apply `string.Normalize(NormalizationForm.FormC)` before comparison.

---

## MEDIUM

### OPS-M01: `OperationResult.Pois` setter is public -- breaks encapsulation
**File:** `SetOperationService.cs:17`
**Severity:** Medium

`public List<Poi> Pois { get; set; }` allows callers to replace the entire list or mutate it freely. This DTO should use `init` or a read-only collection.

---

### OPS-M02: Magic number 0.6 for name similarity threshold
**File:** `PoiMatcher.cs:26`
**Severity:** Medium

```csharp
return NameSimilarity(a.Name, b.Name) >= 0.6;
```

The 0.6 threshold is a magic number with no explanation. Is this value empirically validated? For short names like "Cafe" vs. "Cave", the Levenshtein similarity is 0.75, which passes. But "Cafe" and "Cape" also yields 0.75. For a 3-letter name like "Bar" vs. "Bay", similarity is 0.67 -- also passes. This threshold is too permissive for short names and may be too strict for long names.

**Fix:** Extract to a named constant with a doc comment explaining the rationale. Consider a length-adjusted threshold.

---

### OPS-M03: `toleranceMeters` default of 100 is undocumented and may be wrong
**File:** `PoiMatcher.cs:12`, `SetOperationService.cs:33`
**Severity:** Medium

100 meters is hardcoded as the default in two separate places (duplication). Whether 100m is appropriate depends entirely on context: in a dense urban area, 100m could span multiple restaurants on the same block. In rural areas, the same POI's coordinates from different sources may differ by more than 100m.

---

### OPS-M04: `ExecuteAsync` creates a `DbContext` that outlives the operation
**File:** `SetOperationService.cs:35-45`
**Severity:** Medium (Resource management)

The `DbContext` is created and disposed with `await using`, which is correct. However, the binary operations call `GetCollectionPois` twice (once for A in `ExecuteAsync`, once for B in `ExecuteBinaryOp`), loading potentially huge object graphs into the same context's change tracker. For large collections, this causes memory bloat and slow change detection. Consider using `AsNoTracking()` since these are read-only queries.

---

### OPS-M05: `CommitResultAsync` does two `SaveChangesAsync` calls
**File:** `SetOperationService.cs:114, 129`
**Severity:** Medium (Correctness)

The first `SaveChangesAsync` saves the collection to get its ID, then the second saves the items. If the second `SaveChangesAsync` fails (e.g., FK violation, connection drop), you have an empty collection in the database with no items. There is no transaction wrapping both operations.

**Fix:** Wrap both saves in an explicit `IDbContextTransaction` or use `HiLo` ID generation to get the collection ID before the first save.

---

### OPS-M06: `Description` strings in operation results are misleading
**File:** `SetOperationService.cs:56, 61`
**Severity:** Medium (Correctness)

```csharp
Description = $"A - B: {poisA.Count} - {poisB.Count} matches"
```

This says "matches" but shows the counts of the input collections, not the number of matches found. The actual result count is not included. For Intersect, the description says "common POIs between X and Y" but does not say how many were found. The user has to count `Pois.Count` themselves.

---

### OPS-M07: `Union` does not deduplicate *within* collection A
**File:** `SetOperationService.cs:68-83`
**Severity:** Medium (Correctness)

`ExecuteUnion` starts with `new List<Poi>(poisA)` (all of A) and adds items from B that have no match in A. But if collection A itself contains duplicates, they are all preserved. A true set union should produce a set with no duplicates. This is inconsistent with the `Dedup` operation existing separately.

---

### OPS-M08: `FindMatch` returns first match, not best match
**File:** `PoiMatcher.cs:33-35`
**Severity:** Medium (Correctness)

```csharp
return candidates.FirstOrDefault(c => IsMatch(poi, c, toleranceMeters));
```

When multiple candidates match, this returns whichever happens to be enumerated first. It should return the best match (closest distance, highest name similarity). This means match results are order-dependent and non-deterministic if the input enumerable order is not guaranteed.

---

### OPS-M09: Thread safety -- `PoiMatcher` is stateless but not documented as such
**File:** `PoiMatcher.cs`
**Severity:** Medium

`PoiMatcher` has no instance fields, so it is technically thread-safe. However, it is registered in DI (injected into `SetOperationService`) with an unknown lifetime. If registered as singleton (which it should be, since stateless), consumers need to know it is safe for concurrent use. The methods should be `static` or the class should implement an interface with a doc comment about thread safety.

Since there is no state, all methods should simply be `static`. The class is pretending to be a service when it is really a utility.

---

## LOW

### OPS-L01: `Levenshtein` allocates a 2D array instead of using two rows
**File:** `PoiMatcher.cs:94`
**Severity:** Low (already covered in OPS-C04 for severity, noting the algorithmic alternative here)

The textbook Levenshtein uses `int[n+1, m+1]`. The standard optimization uses two 1D arrays of size `min(n,m)+1`, reducing memory from O(n*m) to O(min(n,m)).

---

### OPS-L02: `NameSimilarity` has dead code path
**File:** `PoiMatcher.cs:86`
**Severity:** Low

```csharp
if (maxLen == 0) return 1.0;
```

This line is unreachable. The method already returns 0 on line 75 if either string is null or empty. If both strings are non-empty, `maxLen` is always >= 1. If both strings are empty after trimming, `a == b` on line 80 returns `true` first. The only way to reach line 86 with `maxLen == 0` is if both strings are empty, which is already handled.

---

### OPS-L03: `SetOperation` enum should not be in `SetOperationService.cs`
**File:** `SetOperationService.cs:7-13`
**Severity:** Low (Organization)

The `SetOperation` enum and `OperationResult` class are defined in the same file as `SetOperationService`. They should be in their own files. `OperationResult` especially -- it is a public DTO that other layers will reference.

---

### OPS-L04: `PoiMatcher.NormalizeUrl` is private but should be testable
**File:** `PoiMatcher.cs:112`
**Severity:** Low

URL normalization is complex logic (see OPS-H03) that needs dedicated unit tests. Being `private static`, it can only be tested indirectly through `IsMatch`. Make it `internal` with `[InternalsVisibleTo]` for the test project, or extract it to a `UrlNormalizer` utility class.

---

### OPS-L05: Inconsistent null handling for `GoogleMapsUrl`
**File:** `PoiMatcher.cs:15`, `Poi.cs:9`
**Severity:** Low

`Poi.GoogleMapsUrl` is `string?` (nullable). `IsMatch` checks `!string.IsNullOrEmpty` for both. But if only ONE POI has a URL, the code silently skips Tier 1 and falls through to Tier 2. This is technically correct but undocumented. A POI with a URL and a POI without one can still match via proximity/name, which may or may not be desirable.

---

### OPS-L06: No input validation on `ExecuteAsync`
**File:** `SetOperationService.cs:33`
**Severity:** Low

`collectionAId` is not validated. If it refers to a non-existent collection, `GetCollectionPois` returns an empty list silently. The caller gets an empty `OperationResult` with no indication that the collection ID was invalid. Should throw or return an error.

---

### OPS-L07: `CommitResultAsync` parameters `name` and `color` are not validated
**File:** `SetOperationService.cs:101`
**Severity:** Low

`name` can be null, empty, or whitespace. `color` has a default of `"#7c3aed"` but callers can pass any string including invalid hex colors. No validation.

---

### OPS-L08: Haversine edge case -- identical coordinates
**File:** `GeoUtils.cs:7-15`
**Severity:** Low

When both points are identical, `dLat = 0`, `dLon = 0`, `a = 0`, `c = 0`, result is `0.0`. This is correct, but worth noting that floating-point imprecision could theoretically produce a tiny negative value in `a` due to rounding, which would make `Math.Sqrt(a)` return `NaN`. In practice this does not happen with IEEE 754 for the Haversine formula, but adding a `Math.Max(a, 0)` clamp would be defensive.

---

### OPS-L09: `Contains` for substring matching is culturally insensitive
**File:** `PoiMatcher.cs:81`
**Severity:** Low

```csharp
if (a.Contains(b) || b.Contains(a)) return 0.9;
```

`String.Contains` uses ordinal comparison by default in .NET. This means "strasse" does not match "stra\u00dfe" (German sharp s). For an international maps application, consider using `StringComparison.CurrentCultureIgnoreCase` or normalizing further.

---

## SUMMARY

| Severity | Count |
|----------|-------|
| Critical | 4     |
| High     | 6     |
| Medium   | 9     |
| Low      | 9     |
| **Total** | **28** |

The core issue is architectural: matching is done via brute-force pairwise comparison with no spatial indexing, no URL pre-indexing, and O(n*m) allocation-heavy Levenshtein calls. This makes every set operation quadratic. The `CommitResultAsync` method adds N+1 queries on top of that for no reason. Until a spatial index and URL hash-map are added, this service is a landmine waiting for someone to click "Union" on two 10k-POI collections.
