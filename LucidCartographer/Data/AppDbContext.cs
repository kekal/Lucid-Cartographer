using LucidCartographer.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Poi> Pois => Set<Poi>();
    public DbSet<PoiImage> PoiImages => Set<PoiImage>();
    public DbSet<PoiCollection> PoiCollections => Set<PoiCollection>();
    public DbSet<PoiCollectionItem> PoiCollectionItems => Set<PoiCollectionItem>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<PoiTag> PoiTags => Set<PoiTag>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RouteSegment> RouteSegments => Set<RouteSegment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Poi>(entity =>
        {
            // All MaxLength via Fluent API only (removed [MaxLength] attributes from entity — DRY)
            entity.Property(e => e.Name).HasMaxLength(500);
            entity.Property(e => e.GoogleMapsUrl).HasMaxLength(2048);
            entity.Property(e => e.Address).HasMaxLength(1000);
            entity.Property(e => e.Category).HasMaxLength(100);
            entity.Property(e => e.Notes).HasMaxLength(10000);
            entity.Property(e => e.Website).HasMaxLength(2048);
            entity.Property(e => e.Phone).HasMaxLength(50);
            entity.Property(e => e.ImageUrl).HasMaxLength(2048);
            entity.Property(e => e.Country).HasMaxLength(200);
            entity.Property(e => e.Region).HasMaxLength(200);

            entity.Property(e => e.AddedDate).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasIndex(e => e.GoogleMapsUrl);
            entity.HasIndex(e => new { e.Latitude, e.Longitude });
            entity.HasIndex(e => e.Name);
            // IsEnriched is a pure data state now, still queried by
            // startup revive and the failed-enrichment count.
            entity.HasIndex(e => e.IsEnriched);
            // The enrichment queue: PoiEnrichmentBackgroundService pages
            // through rows that explicitly requested enrichment.
            entity.HasIndex(e => new { e.EnrichmentRequested, e.EnrichmentFailureCount, e.LastEnrichmentAttemptAt });

            // Check constraints for data integrity
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Poi_Latitude", "Latitude IS NULL OR (Latitude >= -90 AND Latitude <= 90)");
                t.HasCheckConstraint("CK_Poi_Longitude", "Longitude IS NULL OR (Longitude >= -180 AND Longitude <= 180)");
                t.HasCheckConstraint("CK_Poi_Rating", "Rating IS NULL OR (Rating >= 1 AND Rating <= 5)");
                t.HasCheckConstraint("CK_Poi_GoogleRating", "GoogleRating IS NULL OR (GoogleRating >= 1.0 AND GoogleRating <= 5.0)");
                t.HasCheckConstraint("CK_Poi_ReviewCount", "ReviewCount IS NULL OR ReviewCount >= 0");
                t.HasCheckConstraint("CK_Poi_EnrichmentFailureCount", "EnrichmentFailureCount >= 0");
            });
        });

        modelBuilder.Entity<PoiImage>(entity =>
        {
            entity.HasKey(e => e.PoiId);
            entity.Property(e => e.ContentType).HasMaxLength(100);
            entity.HasOne(e => e.Poi)
                .WithOne(p => p.Image)
                .HasForeignKey<PoiImage>(e => e.PoiId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PoiCollection>(entity =>
        {
            entity.Property(e => e.Name).HasMaxLength(500);
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.Color).HasMaxLength(7);
            entity.Property(e => e.IconName).HasMaxLength(100);
            entity.Property(e => e.SourceType).HasMaxLength(50);
            entity.Property(e => e.SourceFileName).HasMaxLength(500);

            entity.Property(e => e.CreatedDate).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasIndex(e => e.Name);

            // TRIP-SCHEMA-03: Trip-lens columns + Start/Finish FKs.
            entity.Property(e => e.TravelMode).HasMaxLength(20);

            // FK-id-only relationships to Poi (no inverse collection on Poi). Deleting a
            // Start/Finish POI nulls the reference rather than cascading the Collection away.
            entity.HasOne<Poi>()
                .WithMany()
                .HasForeignKey(e => e.StartPoiId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<Poi>()
                .WithMany()
                .HasForeignKey(e => e.FinishPoiId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.StartPoiId);
            entity.HasIndex(e => e.FinishPoiId);

            // TRIP-SCHEMA-01: string-persisted enum constrained at the DB level (CK_<Table>_<Column>
            // style, like CK_Poi_*). SQL is built from TravelMode.All so it can never drift.
            entity.ToTable(t =>
                t.HasCheckConstraint("CK_PoiCollection_TravelMode",
                    EnumCheckSql("TravelMode", TravelMode.All)));
        });

        modelBuilder.Entity<PoiCollectionItem>(entity =>
        {
            // Composite PK replaces surrogate Id (REVIEW-24)
            entity.HasKey(e => new { e.PoiId, e.PoiCollectionId });

            entity.HasOne(e => e.Poi)
                .WithMany(p => p.CollectionItems)
                .HasForeignKey(e => e.PoiId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.PoiCollection)
                .WithMany(c => c.CollectionItems)
                .HasForeignKey(e => e.PoiCollectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Tag>(entity =>
        {
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.HasIndex(e => e.Name).IsUnique();
        });

        modelBuilder.Entity<PoiTag>(entity =>
        {
            entity.HasKey(e => new { e.PoiId, e.TagId });

            entity.HasOne(e => e.Poi)
                .WithMany(p => p.PoiTags)
                .HasForeignKey(e => e.PoiId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Tag)
                .WithMany(t => t.PoiTags)
                .HasForeignKey(e => e.TagId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Session>(entity =>
        {
            entity.Property(e => e.TokenHash).HasMaxLength(64);
            entity.HasIndex(e => e.TokenHash).IsUnique();
            entity.HasIndex(e => e.ExpiresAt);
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.Property(e => e.Username).HasMaxLength(200);
            entity.Property(e => e.PasswordHash).HasMaxLength(512);
            entity.HasIndex(e => e.Username).IsUnique();
        });

        modelBuilder.Entity<RouteSegment>(entity =>
        {
            // TRIP-CACHE-01: directional composite key — A→B and B→A are distinct rows.
            entity.HasKey(e => new { e.FromPoiId, e.ToPoiId, e.TravelMode });

            entity.Property(e => e.TravelMode).HasMaxLength(20);
            entity.Property(e => e.Fidelity).HasMaxLength(20);
            entity.Property(e => e.Source).HasMaxLength(100);

            // Cached legs are invalidated when either endpoint POI is deleted.
            entity.HasOne<Poi>()
                .WithMany()
                .HasForeignKey(e => e.FromPoiId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Poi>()
                .WithMany()
                .HasForeignKey(e => e.ToPoiId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.FromPoiId);
            entity.HasIndex(e => e.ToPoiId);

            // TRIP-SCHEMA-01: string-persisted enums constrained at the DB level. SQL is built
            // from TravelMode.All / Fidelity.All so the constraints can never drift.
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("CK_RouteSegment_TravelMode",
                    EnumCheckSql("TravelMode", TravelMode.All));
                t.HasCheckConstraint("CK_RouteSegment_Fidelity",
                    EnumCheckSql("Fidelity", Fidelity.All));
            });
        });
    }

    // TRIP-SCHEMA-01: single source of truth for the string-enum CHECK constraints. Produces e.g.
    // "TravelMode IN ('AnyAir','Drive','Walk','Cycle')" directly from the enum's All list, so adding
    // a value to TravelMode.All / Fidelity.All cannot silently diverge from the DB constraint.
    private static string EnumCheckSql(string column, IReadOnlyList<string> allowed) =>
        $"{column} IN ({string.Join(",", allowed.Select(v => $"'{v}'"))})";

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        SetTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        SetTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void SetTimestamps()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<Poi>())
        {
            switch (entry.State)
            {
                case EntityState.Added when entry.Entity.AddedDate == default:
                    entry.Entity.AddedDate = now;
                    break;
                case EntityState.Modified:
                    entry.Entity.Version++;
                    break;
            }
        }
        foreach (var entry in ChangeTracker.Entries<PoiCollection>())
        {
            switch (entry.State)
            {
                case EntityState.Added when entry.Entity.CreatedDate == default:
                    entry.Entity.CreatedDate = now;
                    break;
                case EntityState.Modified:
                    entry.Entity.Version++;
                    break;
            }
        }
    }
}
