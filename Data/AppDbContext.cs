using LucidCartographer.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<Poi> Pois => Set<Poi>();
        public DbSet<PoiCollection> PoiCollections => Set<PoiCollection>();
        public DbSet<PoiCollectionItem> PoiCollectionItems => Set<PoiCollectionItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Poi>(entity =>
            {
                entity.Property(e => e.Name).HasMaxLength(500);
                entity.Property(e => e.GoogleMapsUrl).HasMaxLength(2048);
                entity.Property(e => e.Address).HasMaxLength(1000);
                entity.Property(e => e.Category).HasMaxLength(100);
                entity.Property(e => e.Status).HasMaxLength(50);
                entity.Property(e => e.Tags).HasMaxLength(2000);
                entity.Property(e => e.Notes).HasMaxLength(10000);
                entity.Property(e => e.Website).HasMaxLength(2048);
                entity.Property(e => e.Phone).HasMaxLength(50);
                entity.Property(e => e.ImageUrl).HasMaxLength(2048);
                entity.Property(e => e.Country).HasMaxLength(200);
                entity.Property(e => e.Region).HasMaxLength(200);

                entity.Property(e => e.AddedDate).HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.HasIndex(e => e.GoogleMapsUrl);
                entity.HasIndex(e => new { e.Latitude, e.Longitude });
                entity.HasIndex(e => e.Status);
                entity.HasIndex(e => e.Name);

                // Check constraints for data integrity
                entity.ToTable(t =>
                {
                    t.HasCheckConstraint("CK_Poi_Latitude", "Latitude >= -90 AND Latitude <= 90");
                    t.HasCheckConstraint("CK_Poi_Longitude", "Longitude >= -180 AND Longitude <= 180");
                    t.HasCheckConstraint("CK_Poi_Rating", "Rating IS NULL OR (Rating >= 1 AND Rating <= 5)");
                    t.HasCheckConstraint("CK_Poi_GoogleRating", "GoogleRating IS NULL OR (GoogleRating >= 1.0 AND GoogleRating <= 5.0)");
                    t.HasCheckConstraint("CK_Poi_ReviewCount", "ReviewCount IS NULL OR ReviewCount >= 0");
                });
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
            });

            modelBuilder.Entity<PoiCollectionItem>(entity =>
            {
                entity.HasIndex(e => new { e.PoiId, e.PoiCollectionId }).IsUnique();

                entity.HasOne(e => e.Poi)
                    .WithMany(p => p.CollectionItems)
                    .HasForeignKey(e => e.PoiId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(e => e.PoiCollection)
                    .WithMany(c => c.CollectionItems)
                    .HasForeignKey(e => e.PoiCollectionId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }

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
                if (entry.State == EntityState.Added && entry.Entity.AddedDate == default)
                {
                    entry.Entity.AddedDate = now;
                }
                if (entry.State == EntityState.Modified)
                {
                    entry.Entity.Version++;
                }
            }
            foreach (var entry in ChangeTracker.Entries<PoiCollection>())
            {
                if (entry.State == EntityState.Added && entry.Entity.CreatedDate == default)
                {
                    entry.Entity.CreatedDate = now;
                }
                if (entry.State == EntityState.Modified)
                {
                    entry.Entity.Version++;
                }
            }
        }
    }
}
