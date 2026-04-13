# REVIEW 2 -- OPERATIONS & MATCHING SERVICES

**Reviewer:** Principal Engineer (angry, pedantic)
**Date:** 2026-04-13
**Scope:** `PoiMatcher.cs`, `IPoiMatcher.cs`, `SetOperationService.cs`, `ISetOperationService.cs`, `GeoUtils.cs`

---

## OPS-R01 | CRITICAL | FindMatch double-computes Haversine distance

**File:** `PoiMatcher.cs`, lines 56-75

The simple `FindMatch(Poi, IEnumerable<Poi>)` overload calls `IsMatch()` which internally
calls `GeoUtils.HaversineDistance()`, and then -- if the match passes -- calls
`GeoUtils.HaversineDistance()` **again** on line 66 to rank by distance. Two trig-heavy
Haversine calculations per candidate, every single time.

For the indexed overload (lines 81-123), the inline proximity loop does NOT have this
problem because it computes distance once and checks threshold inline. But callers using
the simple overload (and the `FindDuplicateGroups` O(n^2) loop which calls `IsMatch`)
pay the double-computation tax on every pair.

**Impact:** For `FindDuplicateGroups` on N POIs, that is N*(N-1)/2 pairs, each doing up
to 2 Haversine calls instead of 1. On 5000 POIs that is ~25 million unnecessary trig
operations.

**Fix:** Refactor `IsMatch` to accept an optional `out double distance` parameter or
create an internal `IsMatchWithDistance` that returns the computed distance, so `FindMatch`
can reuse it.

---

## OPS-R02 | MAJOR | NameSimilarity threshold is not configurable in IsMatch

**File:** `PoiMatcher.cs`, line 49; `IPoiMatcher.cs`

`IsMatch` accepts a configurable `toleranceMeters` parameter but the name similarity
threshold is hardcoded to `DefaultNameSimilarityThreshold` (0.6) on line 49 and again
on line 112. The spatial tolerance is tunable per-call, but the name similarity threshold
is not.

This is an asymmetric API. If someone needs tighter matching (e.g., 0.8 for high-confidence
dedup), they cannot achieve it without subclassing. The threshold should be a parameter on
`IsMatch`, `FindMatch`, and `FindDuplicateGroups`, or at minimum injected at construction.

---

## OPS-R03 | MAJOR | NormalizeUrl is instance method but stateless -- exposes implementation on interface

**File:** `PoiMatcher.cs`, line 268; `IPoiMatcher.cs`, line 65

`NormalizeUrl` is a public instance method and is exposed on `IPoiMatcher`. URL normalization
is a pure function with zero dependency on instance state. It should be `static`. Exposing it
on the interface forces every `IPoiMatcher` implementation to provide URL normalization, which
is an interface segregation violation (ISP).

`BuildUrlIndex` calls `NormalizeUrl` via `this`, but it could call a static method. The only
reason it cannot be static today is that it uses source-generated `partial` regex methods,
which require a partial class instance. However, the regex methods themselves are `static
partial` -- the generated code IS static. This is a red herring. The method could be static.

---

## OPS-R04 | MAJOR | GetCollectionPois can return null navigation properties

**File:** `SetOperationService.cs`, lines 192-199

```csharp
.Select(ci => ci.Poi)
```

This selects the `Poi` navigation property from `PoiCollectionItem`. If the EF query does not
eager-load (`Include`) the navigation property, and tracking is disabled (`AsNoTracking`),
the `Poi` property will be `null` for every item. The query relies on EF Core's implicit
join behavior in the `Select` projection, which DOES work in EF Core 7+ -- but only because
EF translates `.Select(ci => ci.Poi)` into a SQL JOIN.

However, if `Poi` has been deleted but the `PoiCollectionItem` still exists (orphaned
foreign key with no cascade configured), this projection silently produces `null` entries
in the list. The return type is `List<Poi>` (non-nullable), but the list can contain nulls.
Downstream code (`IsMatch`, `HaversineDistance`) will throw NullReferenceException.

**Fix:** Add `.Where(ci => ci.Poi != null)` or, better, ensure referential integrity with
cascade delete and a non-nullable FK constraint.

---

## OPS-R05 | MAJOR | Union is asymmetric -- does not deduplicate within A

**File:** `SetOperationService.cs`, lines 122-136

`ExecuteUnion` starts with `new List<Poi>(poisA)` (all of A), then adds items from B that
do not match anything in A. But if A itself contains duplicates, they are all preserved.
A true set union should contain each unique element exactly once. If the user has duplicates
within collection A, the "union" result has duplicates.

This is mathematically wrong for a set operation named "Union."

**Fix:** Either run dedup on A first, or document that this is a "merge" not a set union.

---

## OPS-R06 | MODERATE | Intersect returns POIs from A only -- loses B metadata

**File:** `SetOperationService.cs`, lines 112-119

`ExecuteIntersect` returns POIs from collection A that have a match in B. This means if B
has richer metadata (e.g., a GoogleMapsUrl, a rating, notes) for the same place, that data
is silently discarded. An intersection should arguably merge or let the caller choose which
side's POI to keep, or return both.

At minimum this behavior should be documented: "Intersect returns A-side POIs."

---

## OPS-R07 | MODERATE | FindDuplicateGroups is O(n^2) with no early termination or spatial indexing

**File:** `PoiMatcher.cs`, lines 129-181

The nested loop on lines 156-165 is O(n^2). For large collections (10K+ POIs), this is
~50 million pair comparisons, each involving Haversine (trig) and potentially Levenshtein
(O(m*k) string DP). No spatial index (R-tree, geohash grid, k-d tree) is used to prune
candidates.

For a 100m tolerance, the vast majority of pairs are thousands of kilometers apart and
could be eliminated in O(1) with a geohash bucket or latitude pre-filter:
`|lat1 - lat2| > tolerance_in_degrees` is a trivial O(1) rejection. 100m is roughly
0.0009 degrees of latitude. A simple pre-filter would eliminate 99%+ of pairs.

---

## OPS-R08 | MODERATE | CID regex does not handle `ftid` parameter (Google Maps)

**File:** `PoiMatcher.cs`, lines 287-293, 346

The CID regex `[?&]cid=(\d+)` only matches the `cid` query parameter. Google Maps also
uses `ftid=0x...:0x...` as a place identifier in many URL formats (especially the newer
`/maps/place/` URLs). Two URLs pointing to the same place -- one with `cid=`, one with
`ftid=` -- will NOT match via Tier 1, falling through to proximity matching or (worse)
returning false if both have URLs (OPS-H05 logic).

---

## OPS-R09 | MODERATE | URL normalization does not handle URL-encoded characters

**File:** `PoiMatcher.cs`, lines 268-312

The normalizer does not decode percent-encoded characters. The URLs
`https://maps.google.com/maps?q=caf%C3%A9` and
`https://maps.google.com/maps?q=cafe%CC%81` (different Unicode encodings of the same
word) will not match. More practically, `%20` vs `+` in query strings, or
`%2F` vs `/` in path segments, cause false mismatches.

`Uri.UnescapeDataString()` should be applied before normalization.

---

## OPS-R10 | MODERATE | NameSimilarity substring match returns arbitrary 0.9

**File:** `PoiMatcher.cs`, line 218

```csharp
if (a.Contains(b) || b.Contains(a)) return 0.9;
```

This returns a hardcoded 0.9 when one name is a substring of the other. "A" is a substring
of "Absolutely Everything Restaurant." This is not a 90% match. The heuristic has no
length-ratio guard. A name of length 1 is a substring of every other name.

**Fix:** Add a minimum length ratio check, e.g., `(double)shorter.Length / longer.Length > 0.5`.

---

## OPS-R11 | MODERATE | BuildUrlIndex silently drops duplicate URLs (first-one-wins)

**File:** `PoiMatcher.cs`, lines 187-199

`TryAdd` on line 196 silently discards the second (and subsequent) POIs that share the
same normalized URL. No logging, no diagnostic, no return value indicating collisions.
If two POIs legitimately share a URL (e.g., a restaurant and its bar, both on the same
Google Maps page), one is silently invisible to all URL-based lookups.

For `FindMatch`, this means the "wrong" POI could be returned as the match because the
index only contains the first one encountered, which depends on enumeration order.

---

## OPS-R12 | MINOR | Interface default parameters reference concrete class constants

**File:** `IPoiMatcher.cs`, lines 21, 31, 41, 50; `ISetOperationService.cs`, line 23

Every method on `IPoiMatcher` and `ISetOperationService` has a default parameter value of
`PoiMatcher.DefaultToleranceMeters`. An interface referencing a concrete implementation's
constant is a dependency inversion violation. If a second implementation of `IPoiMatcher`
is created, it is still bound to `PoiMatcher`'s constant.

**Fix:** Define the constants on the interface (or a shared static class) and have
`PoiMatcher` reference the interface constant.

---

## OPS-R13 | MINOR | EarthRadiusMeters should be const, not static readonly

**File:** `GeoUtils.cs`, line 6

```csharp
private static readonly double EarthRadiusMeters = 6371000;
```

This is a compile-time literal. There is no reason for it to be `static readonly` (heap
allocated, read via field load at runtime). It should be `const double` which inlines the
value at compile time. For a method called millions of times in O(n^2) loops, this is a
micro-optimization that costs nothing to do correctly.

---

## OPS-R14 | MINOR | CommitResultAsync does not validate input

**File:** `SetOperationService.cs`, lines 157-187

No null check on `pois`. No null/empty check on `name`. No validation that the POI IDs
actually exist in the database. If `pois` contains POIs with `Id = 0` (unsaved entities),
the `PoiCollectionItem` FK will point to nothing or throw.

The transaction will succeed, creating a collection with dangling foreign keys.

---

## OPS-R15 | MINOR | Levenshtein allocates two arrays per call -- no pooling

**File:** `PoiMatcher.cs`, lines 230-261

`LevenshteinDistance` allocates `new int[sLen + 1]` twice per call. In the O(n^2)
`FindDuplicateGroups` loop, this means millions of small array allocations that pressure
the GC. `ArrayPool<int>.Shared.Rent()` would eliminate this allocation entirely.

---

## OPS-R16 | MINOR | OperationResult is a class with init-only properties but no validation

**File:** `SetOperationService.cs`, lines 26-35

`OperationResult` is a mutable class (despite `init`-only setters, the `List<Poi>` itself
is mutable). It should be a `record` for value semantics, or at minimum the `Pois` list
should be exposed as `IReadOnlyList<Poi>` to prevent callers from mutating the result.

---

## OPS-R17 | MINOR | Missing null guard on IsMatch parameters

**File:** `PoiMatcher.cs`, line 32

`IsMatch(Poi a, Poi b, ...)` does not null-check `a` or `b`. A null POI will throw
`NullReferenceException` at `a.GoogleMapsUrl` with no meaningful stack context.
`ArgumentNullException.ThrowIfNull()` costs one line and provides a named parameter.

---

## OPS-R18 | MINOR | SetOperation enum is defined inside SetOperationService.cs alongside OperationResult

**File:** `SetOperationService.cs`, lines 10-20, 25-35

The `SetOperation` enum and `OperationResult` class are defined in the same file as
`SetOperationService`. These are public types used across the codebase (including the
interface file). Each public type should live in its own file per C# conventions.

---

## OPS-R19 | NITPICK | Regex for www prefix only matches https scheme

**File:** `PoiMatcher.cs`, line 342

```csharp
[GeneratedRegex(@"^https://www\.", RegexOptions.IgnoreCase)]
```

The scheme normalization on lines 281-282 converts `http://` to `https://` before this
regex runs, so in practice this works. But the regex is semantically misleading -- its
name is `WwwPrefixRegex` but it also matches (and replaces) the scheme. If the
normalization order ever changes, this silently breaks. The regex replacement string
`"https://"` bakes in a scheme assumption.

---

## OPS-R20 | NITPICK | No cancellation token forwarded in O(n^2) loops

**File:** `PoiMatcher.cs`, lines 156-165

`FindDuplicateGroups` takes no `CancellationToken`. For large inputs, this is a
long-running CPU-bound operation that cannot be cancelled. The caller
(`SetOperationService.ExecuteAsync`) accepts a token but never passes it to the matcher.

---

---

## SUMMARY TABLE

| ID | Severity | File(s) | Issue |
|---------|----------|-------------------------------|-----------------------------------------------|
| OPS-R01 | CRITICAL | PoiMatcher.cs | Double Haversine computation in FindMatch |
| OPS-R02 | MAJOR | PoiMatcher.cs, IPoiMatcher.cs | Name similarity threshold not configurable |
| OPS-R03 | MAJOR | PoiMatcher.cs, IPoiMatcher.cs | Stateless method on interface (ISP violation) |
| OPS-R04 | MAJOR | SetOperationService.cs | Possible null POIs from orphaned FKs |
| OPS-R05 | MAJOR | SetOperationService.cs | Union does not deduplicate within A |
| OPS-R06 | MODERATE | SetOperationService.cs | Intersect discards B-side metadata |
| OPS-R07 | MODERATE | PoiMatcher.cs | O(n^2) dedup with no spatial pruning |
| OPS-R08 | MODERATE | PoiMatcher.cs | CID regex ignores ftid parameter |
| OPS-R09 | MODERATE | PoiMatcher.cs | No URL percent-decoding before normalize |
| OPS-R10 | MODERATE | PoiMatcher.cs | Substring match returns 0.9 with no len guard |
| OPS-R11 | MODERATE | PoiMatcher.cs | BuildUrlIndex silently drops dupe URLs |
| OPS-R12 | MINOR | IPoiMatcher.cs, ISetOp...cs | Interface defaults reference concrete class |
| OPS-R13 | MINOR | GeoUtils.cs | static readonly should be const |
| OPS-R14 | MINOR | SetOperationService.cs | CommitResultAsync has no input validation |
| OPS-R15 | MINOR | PoiMatcher.cs | Levenshtein allocates arrays with no pooling |
| OPS-R16 | MINOR | SetOperationService.cs | OperationResult exposes mutable List |
| OPS-R17 | MINOR | PoiMatcher.cs | No null guards on IsMatch parameters |
| OPS-R18 | MINOR | SetOperationService.cs | Multiple public types in one file |
| OPS-R19 | NITPICK | PoiMatcher.cs | Www regex semantically misleading |
| OPS-R20 | NITPICK | PoiMatcher.cs | No CancellationToken in O(n^2) loop |

**Totals:** 1 Critical, 4 Major, 6 Moderate, 7 Minor, 2 Nitpick = **20 findings**

---

## ELEGANCE SCORE: 5 / 10

The bones are decent. The union-find approach for transitive grouping is correct. The
two-tier URL/proximity matching strategy is sound in concept. The Levenshtein two-row
optimization shows someone was thinking about space complexity. The URL normalization
handles several real-world cases (CID extraction, tracking param removal, www stripping).

But the execution is sloppy. Double Haversine computation in the hot path is inexcusable.
An O(n^2) loop with no spatial pruning, no cancellation, and per-call array allocations
will fall over on any dataset above toy size. The "Union" that does not deduplicate is
mathematically wrong. The interface references concrete class constants. The substring
heuristic in NameSimilarity will match single characters to restaurant names at 90%
confidence, which is absurd. URL normalization misses percent-encoding and ftid, meaning
Tier 1 will silently fail for a meaningful fraction of real Google Maps URLs.

Someone clearly put effort into the fix cycle, but the result is an implementation that
works on the happy path and crumbles under real-world scale and edge cases.
