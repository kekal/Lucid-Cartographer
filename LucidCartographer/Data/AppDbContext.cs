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
            entity.HasIndex(e => e.IsEnriched);
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

            entity.Property(e => e.TravelMode).HasMaxLength(20);

            // Deleting a Start/Finish POI nulls the reference (not cascade)
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

            // Constraint built from TravelMode.All to prevent drift
            entity.ToTable(t =>
                t.HasCheckConstraint("CK_PoiCollection_TravelMode",
                    EnumCheckSql("TravelMode", TravelMode.All)));
        });

        modelBuilder.Entity<PoiCollectionItem>(entity =>
        {
            entity.HasKey(e => new { e.PoiId, e.PoiCollectionId });

            entity.HasOne(e => e.Poi)
                .WithMany(p => p.CollectionItems)
                .HasForeignKey(e => e.PoiId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.PoiCollection)
                .WithMany(c => c.CollectionItems)
                .HasForeignKey(e => e.PoiCollectionId)
                .OnDelete(DeleteBehavior.Cascade);

            // Null = AnyAir (no "unset" sentinel); constraint allows NULL or TravelMode.All
            entity.Property(e => e.OutgoingTravelMode).HasMaxLength(20);
            entity.ToTable(t =>
                t.HasCheckConstraint("CK_PoiCollectionItem_OutgoingTravelMode",
                    NullableEnumCheckSql("OutgoingTravelMode", TravelMode.All)));
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
            // Directional: A→B and B→A are distinct rows
            entity.HasKey(e => new { e.FromPoiId, e.ToPoiId, e.TravelMode });

            entity.Property(e => e.TravelMode).HasMaxLength(20);
            entity.Property(e => e.Fidelity).HasMaxLength(20);
            entity.Property(e => e.Source).HasMaxLength(100);

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

            // Constraints built from enum .All to prevent drift
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("CK_RouteSegment_TravelMode",
                    EnumCheckSql("TravelMode", TravelMode.All));
                t.HasCheckConstraint("CK_RouteSegment_Fidelity",
                    EnumCheckSql("Fidelity", Fidelity.All));
            });
        });
    }

    // Builds enum CHECK constraint from .All list to prevent drift
    private static string EnumCheckSql(string column, IReadOnlyList<string> allowed) =>
        $"{column} IN ({string.Join(",", allowed.Select(v => $"'{v}'"))})";

    // Nullable variant where NULL is a valid state (null ≡ AnyAir)
    private static string NullableEnumCheckSql(string column, IReadOnlyList<string> allowed) =>
        $"{column} IS NULL OR {EnumCheckSql(column, allowed)}";

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
