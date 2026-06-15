# Story 3.1: Add the per-leg travel-mode column (migration)

Status: done

## Story

As the system, I want a per-leg travel-mode column on the stop membership, so that each leg's mode
can be stored without a separate leg entity.

## Acceptance Criteria

1. **Given** the EF model, **When** the `AddOutgoingTravelMode` migration is created and applied via startup `MigrateAsync`, **Then** `PoiCollectionItem` gains a nullable `OutgoingTravelMode` (string, one of `TravelMode.All` = {AnyAir, Drive, Walk, Cycle}) — the mode of the leg leaving this stop — constrained by the `TravelMode.All` check pattern (TRIP-SCHEMA-01); **And** `null` is semantically identical to AnyAir (one "undefined / Any-Air" state — TRIP-LEGMODE-01); no separate "unset" sentinel.
2. **Given** schema discipline, **When** the migration is authored, **Then** it is a single additive migration applied through `MigrateAsync`; `EnsureCreated` is not used and no applied migration is hand-edited (NFR4); **And** the Trip integration filter runs and stays green after the schema change (NFR8).

## Scope decision — ADD ONLY (readiness Major #1)

**This story ADDS the `OutgoingTravelMode` column only. It does NOT drop `PoiCollection.TravelMode`.**
The readiness report (Major #1) found that dropping `PoiCollection.TravelMode` in 3.1 would break
the build, because the trip-wide→per-leg projection (Story 3.2) and the trip-level mode-selector UI
(Story 3.4) still reference it. Dropping it now violates story independence. So:
- 3.1: additive only — add `OutgoingTravelMode`; leave `PoiCollection.TravelMode` untouched.
- The DROP of `PoiCollection.TravelMode` is deferred to a later Epic-3 story (after 3.2 projection +
  3.4 selector removal eliminate every reference), with its own migration and an AC asserting "no
  remaining references + green build". (RD1a's "dead column until references are gone" fallback.)

## Architecture & Code Context (RD1, TRIP-SCHEMA-01/LEGMODE-01)

- **Entity:** `LucidCartographer/Data/Entities/PoiCollectionItem.cs` (composite PK
  `{PoiId, PoiCollectionId}`; has `OrderIndex`, `DwellMinutes`). Add
  `public string? OutgoingTravelMode { get; set; }` — nullable; `null` ≡ AnyAir (TRIP-LEGMODE-01);
  the mode of the leg LEAVING this stop toward the next stop in Stop Order. Do NOT add an "unset"
  sentinel.
- **DbContext config:** `LucidCartographer/Data/AppDbContext.cs`, the `PoiCollectionItem` entity
  block (~line 105). Add `entity.Property(e => e.OutgoingTravelMode).HasMaxLength(20);` and a CHECK
  constraint allowing NULL or one of `TravelMode.All`. The existing `EnumCheckSql(column, allowed)`
  produces `"col IN ('AnyAir',...)"` which REJECTS NULL — so for this nullable column the constraint
  must be `"OutgoingTravelMode IS NULL OR OutgoingTravelMode IN (...)"`. Add a small nullable variant
  (e.g. a `NullableEnumCheckSql` helper, or inline `$"{col} IS NULL OR " + EnumCheckSql(col, allowed)`),
  named `CK_PoiCollectionItem_OutgoingTravelMode`, built from `TravelMode.All` so it can't drift.
- **Migration:** generate with EF tools (available: `dotnet ef` 8.0.27), do NOT hand-write:
  `dotnet ef migrations add AddOutgoingTravelMode --project LucidCartographer/LucidCartographer.csproj`
  (run from the repo root; ensure it builds first). Review the generated `Up`/`Down` — it should
  `AddColumn<string>("OutgoingTravelMode", "PoiCollectionItems", nullable: true)` and add the CHECK
  constraint (SQLite table-rebuild). Existing rows get NULL (≡ AnyAir) — correct, no backfill needed.
  Update `AppDbContextModelSnapshot.cs` is automatic via the tool.
- **Apply path:** migrations run at startup via `StartupCleanupService.MigrateAsync` (ARCH-CRIT-01).
  Do NOT call `EnsureCreated`. Do NOT hand-edit any already-applied migration.
- **No other change:** `RouteSegment` cache shape + directional `(From,To,Mode)` key unchanged;
  default `Mock` provider unchanged; no VM/service/projection change in THIS story (3.2 reads the
  column). The column is added but not yet consumed — that's fine and independently shippable.

## Constraints (NFRs)

- NFR4 — single additive migration via `MigrateAsync`; check constraint via the `TravelMode.All`
  pattern; never `EnsureCreated`; never hand-edit an applied migration.
- NFR8 — run the Trip integration filter after the schema change; it must stay green.
- Schema discipline: nullable column, `null` ≡ AnyAir; no new cache shape.

## Testing

- A test (or extend an entity/DbContext test) asserting: a `PoiCollectionItem` round-trips
  `OutgoingTravelMode` = each of `TravelMode.All` and `null`; and that an INVALID value is rejected
  by the check constraint (mirror how existing `CK_*_TravelMode` constraints are tested, if such a
  test exists). If the project tests migrations/constraints via the integration host, follow that
  pattern.
- **Run the Trip integration filter** (schema change): it must stay green — this is the load-bearing
  check that the integration host boots with the new column/migration.
- Full fast suite green; mobile green (no behavior change expected).

## Build/Test commands

- Build: `dotnet build LucidCartographer/LucidCartographer.csproj -c Debug`
- Add migration: `dotnet ef migrations add AddOutgoingTravelMode --project LucidCartographer/LucidCartographer.csproj`
- Fast: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName!~Integration"`
- Trip integration: `dotnet test LucidCartographer.Tests/LucidCartographer.Tests.csproj --filter "FullyQualifiedName~Integration&FullyQualifiedName~Trip"`

## Dev Notes

First implementation story of Epic 3 and the feature's ONLY schema migration. ADD-only; the drop of
`PoiCollection.TravelMode` is deferred (Major #1). Story 3.2 reads `OutgoingTravelMode` in the leg
projection.

## Dev Agent Record

ADD-ONLY (Major #1 honored). Added nullable `PoiCollectionItem.OutgoingTravelMode` (string?,
maxlen 20, null ≡ AnyAir per TRIP-LEGMODE-01). `AppDbContext` adds
`CK_PoiCollectionItem_OutgoingTravelMode` via a new `NullableEnumCheckSql` helper built from
`TravelMode.All`: `OutgoingTravelMode IS NULL OR OutgoingTravelMode IN ('AnyAir','Drive','Walk',
'Cycle')` (allows NULL — drift-proof). EF-generated migration `20260615160622_AddOutgoingTravelMode`
(AddColumn + AddCheckConstraint; Down reverses cleanly); snapshot updated.
`PoiCollection.TravelMode` NOT dropped (deferred). `has-pending-model-changes` → none. New
SQLite-backed migration test round-trips all four modes + null and proves the CHECK rejects an
invalid value (positive control + provable CK error).

Adversarial review: 0 CRIT / 0 HIGH / 0 MED / 2 LOW → SHIP. LOW#1 (missing trailing newline)
fixed; LOW#2 (test SQL style) accepted (matches existing convention). Migration SQL byte-identical
across model/migration/snapshot; nullable constraint correct; ADD-only verified. 842 fast + 20
Trip integration green; build clean.

## File List

- LucidCartographer/Data/Entities/PoiCollectionItem.cs (MOD — OutgoingTravelMode)
- LucidCartographer/Data/AppDbContext.cs (MOD — NullableEnumCheckSql + CK constraint)
- LucidCartographer/Migrations/20260615160622_AddOutgoingTravelMode.cs (NEW)
- LucidCartographer/Migrations/20260615160622_AddOutgoingTravelMode.Designer.cs (NEW)
- LucidCartographer/Migrations/AppDbContextModelSnapshot.cs (MOD)
- LucidCartographer.Tests/Services/AddOutgoingTravelModeMigrationTests.cs (NEW)
