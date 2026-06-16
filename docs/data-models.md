# Data Models

_EF Core 8 + SQLite. Source: `Data/AppDbContext.cs`, `Data/Entities/`, `Migrations/`._

## Database Configuration

**Connection string / path resolution** (`Configuration/DatabaseServicesExtensions.cs`, MED-06), in precedence order:

1. `DB_PATH` environment variable
2. `Database:Path` config key (`Database__Path` env form)
3. Default `data/cartographer.db` relative to `ContentRootPath`

Registered as `AddDbContextFactory<AppDbContext>` (SQLite) plus a scoped `AppDbContext` wrapper resolved from the factory (required by OpenIddict managers). OpenIddict entity sets are registered via `.UseOpenIddict()`. The DB's parent directory also holds Data Protection keys and OAuth signing/encryption keys.

## Entities

### Poi (core)
Key fields: `Id` (PK), `Name` (≤500, required), `Latitude`/`Longitude` (`double?`, range-checked), `GoogleMapsUrl`, `Address`, `Category` (constrained to `PoiCategory` constants), `Notes`, `Rating` (1–5), `GoogleRating` (1.0–5.0), `ReviewCount` (≥0), `Website`, `Phone`, `ImageUrl`, `Country`, `Region`, `AddedDate` (UTC default), and the enrichment flags below. `Version` is an optimistic-concurrency token (`[ConcurrencyCheck]`, auto-incremented on `SaveChanges`).

Enrichment flags:
- `IsEnriched` — pure state: true once the background service completes (success **or** soft-fail).
- `EnrichmentRequested` — explicit queue signal. **Creating a POI does NOT set this** (creation is decoupled from enrichment).
- `EnrichmentFailureCount` — incremented per failed attempt, capped at `MaxRetries` (5).
- `LastEnrichmentAttemptAt` — UTC timestamp.
- `EnrichmentNeedsManualUrl` — set when enrichment ran cleanly but found no place; UI prompts the user for a manual URL.

Indexes: `GoogleMapsUrl`, `(Latitude, Longitude)`, `Name`, `IsEnriched`, and `(EnrichmentRequested, EnrichmentFailureCount, LastEnrichmentAttemptAt)` (enrichment-queue paging).
Check constraints: latitude, longitude, rating, googleRating, reviewCount, enrichmentFailureCount.
Navigations: `PoiImage? Image` (1:1, not auto-loaded), `List<PoiCollectionItem>`, `List<PoiTag>`.

### PoiImage
`PoiId` (PK & FK to Poi, 1:1, cascade delete), `Data` (`byte[]` BLOB), `ContentType`. Kept in a separate table so routine POI queries don't drag image bytes; served via `/api/poi-image/{id}`.

### PoiCollection
`Id`, `Name` (≤500), `Description`, `Color` (≤7 hex, default `#005bbf`), `IconName`, `IsVisible` (default true), `CreatedDate`, `SourceType`, `SourceFileName`, `Version` (concurrency token). `PoiCount` is `[NotMapped]` — computed at read time. Index on `Name`.

**Trip Planning fields** (`AddTripPlanning` migration): `StartPoiId`/`FinishPoiId` (nullable FK to Poi, `SetNull`, indexed), `TripStartTime` (`DateTime?`), `TimeBudgetMinutes` (int?, the renamed "Time limit"), `TripViewEnabled` (bool, per-collection Trip View persistence), and the legacy `TravelMode` (string ≤20, default `AnyAir`, check-constrained). Null `FinishPoiId` ⇒ roundtrip. `TimeBudgetMinutes` doubles as the multi-day "Time limit" — set as an HH:MM duration (via the shared `DurationInput`, **no 24h cap** since the compaction milestone) or a finish-by deadline computed **once** as `deadline − start`; Limit and Finish-by are the same canonical value, Finish-by being a derived `start + budget` view (no schema change). The minutes⇄"HH:MM" conversion is centralized in `TravelTimeFormatting.FormatHhmm`/`TryParseHhmm`. **`TravelMode` is no longer the leg driver** (per-leg modes replaced it, FR-23); per RD1a it was kept as a dead-ish column (still written by the inert mobile selector). See [trip-planning.md](./trip-planning.md).

### PoiCollectionItem (join)
Composite PK `(PoiId, PoiCollectionId)`; two cascade FKs. Many-to-many between Poi and PoiCollection. **Trip fields:** `OrderIndex` (int, 1-based Stop Order; 0 = "not a Stop", e.g. unplaceable); `DwellMinutes` (int?, per-membership dwell so the same POI carries different dwell across trips); and — `AddOutgoingTravelMode` migration — **`OutgoingTravelMode`** (string?, ≤20, one of `TravelMode.All`; **null ≡ AnyAir**, one state — TRIP-LEGMODE-01) the mode of the leg **leaving** this stop, check-constrained by `CK_PoiCollectionItem_OutgoingTravelMode`.

### RouteSegment (trip leg / distance-matrix cache)
Composite PK `(FromPoiId, ToPoiId, TravelMode)` — **directional** (`TRIP-CACHE-01`: A→B and B→A are distinct rows). Columns: `DurationSeconds` (int, canonical **seconds**), `DistanceMeters` (double, canonical **meters**), `GeometryPolyline` (string?, null = no road geometry), `Fidelity` (string ≤20, check-constrained), `Source` (≤100), `ComputedAt` (UTC), `Version` (`[ConcurrencyCheck]`). Cascade FKs to `Pois`; indexes on `FromPoiId` and `ToPoiId`. Cache rows are derived/disposable — invalidated on coord/mode/provider/assumed-speed change; never the source of truth for trip intent.

### String-persisted trip enums
`Data/Entities/TravelMode.cs` (`AnyAir`/`Drive`/`Walk`/`Cycle`) and `Data/Entities/Fidelity.cs` (`Measured`/`Estimated`/`Placeholder`/`Manual`) are static string-constant classes (the `PoiCategory` precedent, `TRIP-SCHEMA-01`), each enforced by an EF check constraint built from the class's `All` list (`CK_PoiCollection_TravelMode`, `CK_PoiCollectionItem_OutgoingTravelMode`, `CK_RouteSegment_TravelMode`, `CK_RouteSegment_Fidelity`) so the SQL can never drift from the C# set. The per-leg `OutgoingTravelMode` constraint is nullable (`… IS NULL OR … IN (...)`, via `NullableEnumCheckSql`).

### Tag / PoiTag
`Tag`: `Id`, `Name` (≤200, unique). `PoiTag`: composite PK `(PoiId, TagId)`, two cascade FKs.

### Session (auth tokens)
`Id`, `TokenHash` (≤64, unique — SHA-256 of the opaque cookie token), `CreatedAt`, `ExpiresAt`, `RevokedAt?`. Index on `ExpiresAt`. Expired/revoked rows vacuumed by `StartupCleanupService`.

### User
`Id`, `Username` (≤200, unique), `PasswordHash` (≤512, PBKDF2-SHA256 @ 600k iterations), `CreatedAt`, `LastLoginAt?`.

### OpenIddict entities
`OpenIddictApplications`, `OpenIddictScopes`, `OpenIddictAuthorizations`, `OpenIddictTokens` — managed by `OpenIddict.EntityFrameworkCore` via `.UseOpenIddict()`, in the same SQLite DB.

## Relationships

```
User (standalone)            Session (standalone)        OpenIddict* (OAuth, pkg-managed)

Poi 1───1 PoiImage
Poi *──* PoiCollection   (via PoiCollectionItem, composite PK, cascade)
Poi *──* Tag             (via PoiTag, composite PK, cascade)

PoiCollection ──> Poi    (StartPoiId / FinishPoiId, nullable, SetNull)
RouteSegment  ──> Poi    (FromPoiId / ToPoiId, cascade)  — directional leg cache
```

Most FKs use `DeleteBehavior.Cascade` — deleting a POI removes its image, collection links, tag links, and any `RouteSegment` rows touching it. The exception: a collection's `StartPoiId`/`FinishPoiId` use `SetNull` (deleting the pinned POI just clears the pin).

## Migrations (in order)

| Migration | Purpose |
|-----------|---------|
| InitialCreate | Pois, PoiCollections, Tags, join tables |
| AddPoiImages | PoiImages BLOB table (1:1) |
| AddPoiIsEnriched | `IsEnriched` column |
| PoiCoordsNullable | Make lat/lon nullable |
| AddEnrichmentFailureTracking | `EnrichmentFailureCount`, `LastEnrichmentAttemptAt` |
| AddAuthSessions | Sessions table |
| AddUsers | Users table |
| AddEnrichmentNeedsManualUrl | `EnrichmentNeedsManualUrl` column |
| AddOAuthFrontdoor | OpenIddict tables |
| RemovePoiStatusAndVisitedDate | Drop legacy `Status`, `VisitedDate` |
| AddPoiEnrichmentRequested | `EnrichmentRequested` column |
| AddTripPlanning | Trip fields on PoiCollection/PoiCollectionItem + new `RouteSegments` table + enum check constraints; backfills `OrderIndex` (1..N per collection over placeable members) and `TravelMode` (`AnyAir`) |
| AddOutgoingTravelMode | Per-leg `PoiCollectionItem.OutgoingTravelMode` (nullable, null ≡ AnyAir) + `CK_PoiCollectionItem_OutgoingTravelMode` check constraint. ADD-only — `PoiCollection.TravelMode` was kept as a dead-ish column (RD1a fallback), not dropped |

> Schema changes require a new `dotnet ef migrations add`; migrations are applied at startup via `MigrateAsync` (ARCH-CRIT-01), never `EnsureCreated`. Never hand-edit an applied migration; SQLite has limited `ALTER` support.

## Enrichment State Machine

Decision logic lives in `Services/Enrichment/EnrichmentStateMachine.cs` (pure, no DB):

- **Queue predicate:** `EnrichmentRequested == true && EnrichmentFailureCount < MaxRetries`, ordered by `(EnrichmentFailureCount, LastEnrichmentAttemptAt)`, paged by `BatchSize` (16).
- **Outcomes:** `Resolved` (place found) · `SoftFailure` (page OK, no place → sets `EnrichmentNeedsManualUrl`) · `HardFailure` (exception, retryable, increments failure count).
- On a terminal outcome the worker clears `EnrichmentRequested` and sets `IsEnriched`.
