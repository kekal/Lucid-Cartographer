using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Services;

public class PoiService
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public PoiService(IDbContextFactory<AppDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<List<PoiCollection>> GetCollectionsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.PoiCollections.OrderByDescending(c => c.CreatedDate).ToListAsync();
    }

    public async Task<List<Poi>> GetPoisByCollectionAsync(int collectionId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.PoiCollectionItems
            .Where(ci => ci.PoiCollectionId == collectionId)
            .Select(ci => ci.Poi)
            .ToListAsync();
    }

    public async Task<Dictionary<int, List<Poi>>> GetVisiblePoisGroupedAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var visibleCollectionIds = await db.PoiCollections
            .Where(c => c.IsVisible)
            .Select(c => c.Id)
            .ToListAsync();

        var result = new Dictionary<int, List<Poi>>();
        foreach (var colId in visibleCollectionIds)
        {
            var pois = await db.PoiCollectionItems
                .Where(ci => ci.PoiCollectionId == colId)
                .Select(ci => ci.Poi)
                .ToListAsync();
            result[colId] = pois;
        }
        return result;
    }

    public async Task ToggleVisibilityAsync(int collectionId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var collection = await db.PoiCollections.FindAsync(collectionId);
        if (collection != null)
        {
            collection.IsVisible = !collection.IsVisible;
            await db.SaveChangesAsync();
        }
    }

    public async Task<Poi?> GetPoiAsync(int poiId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Pois.FindAsync(poiId);
    }

    public async Task UpdatePoiAsync(Poi poi)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.Pois.Update(poi);
        await db.SaveChangesAsync();
    }

    public async Task DeleteCollectionAsync(int collectionId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var collection = await db.PoiCollections
            .Include(c => c.CollectionItems)
            .FirstOrDefaultAsync(c => c.Id == collectionId);
        if (collection != null)
        {
            db.PoiCollections.Remove(collection);
            await db.SaveChangesAsync();

            // Clean up orphaned POIs (not in any collection)
            var orphanedPois = await db.Pois
                .Where(p => !p.CollectionItems.Any())
                .ToListAsync();
            db.Pois.RemoveRange(orphanedPois);
            await db.SaveChangesAsync();
        }
    }

    public async Task<List<Poi>> SearchAsync(string query)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var lower = query.ToLowerInvariant();
        return await db.Pois
            .Where(p => p.Name.ToLower().Contains(lower)
                || (p.Address != null && p.Address.ToLower().Contains(lower))
                || (p.Tags != null && p.Tags.ToLower().Contains(lower))
                || (p.Notes != null && p.Notes.ToLower().Contains(lower)))
            .Take(100)
            .ToListAsync();
    }

    public async Task UpdateCollectionColorAsync(int collectionId, string color)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var collection = await db.PoiCollections.FindAsync(collectionId);
        if (collection != null)
        {
            collection.Color = color;
            await db.SaveChangesAsync();
        }
    }
}
