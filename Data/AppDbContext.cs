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
                entity.HasIndex(e => e.GoogleMapsUrl);
                entity.HasIndex(e => new { e.Latitude, e.Longitude });
                entity.HasIndex(e => e.Status);
                entity.HasIndex(e => e.Name);
            });

            modelBuilder.Entity<PoiCollection>(entity =>
            {
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
    }
}
