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

### PoiCollectionItem (join)
Composite PK `(PoiId, PoiCollectionId)`; two cascade FKs. Many-to-many between Poi and PoiCollection.

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
```

All FKs use `DeleteBehavior.Cascade` — deleting a POI removes its image, collection links, and tag links.

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

> Schema changes require a new `dotnet ef migrations add`; migrations are applied at startup via `MigrateAsync` (ARCH-CRIT-01), never `EnsureCreated`. Never hand-edit an applied migration; SQLite has limited `ALTER` support.

## Enrichment State Machine

Decision logic lives in `Services/Enrichment/EnrichmentStateMachine.cs` (pure, no DB):

- **Queue predicate:** `EnrichmentRequested == true && EnrichmentFailureCount < MaxRetries`, ordered by `(EnrichmentFailureCount, LastEnrichmentAttemptAt)`, paged by `BatchSize` (16).
- **Outcomes:** `Resolved` (place found) · `SoftFailure` (page OK, no place → sets `EnrichmentNeedsManualUrl`) · `HardFailure` (exception, retryable, increments failure count).
- On a terminal outcome the worker clears `EnrichmentRequested` and sets `IsEnriched`.
