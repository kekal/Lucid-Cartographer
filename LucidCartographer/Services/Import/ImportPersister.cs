using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Operations;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace LucidCartographer.Services.Import;

/// <summary>
/// Runs a single import persistence pass: creates the target collection,
/// deduplicates parsed POIs against existing rows, inserts the new ones,
/// attaches images and collection links, and saves. Instance state is
/// scoped to one import — construct, <see cref="RunAsync"/>, discard.
///
/// Dedup uses <see cref="PoiIdentity.AreSamePlace(Poi?, Poi?)"/> — the
/// single source of truth for "same real place". Name similarity plus
/// geographic proximity, no URL tier: distinct franchise branches that
/// share a corporate URL stay distinct; rows pending enrichment at
/// (0,0) are never collapsed by coincidence.
/// </summary>
internal sealed class ImportPersister(
    AppDbContext db,
    ILogger logger,
    IReadOnlyList<ImportedPoi> validParsed,
    int totalParsed,
    string collectionName,
    string color,
    string sourceType,
    string? sourceFileName,
    CancellationToken ct)
{
    private const string ImportedStatus = "imported";
    private const string GoogleScrapeSourceType = "google_maps_scrape";

    // State built up during RunAsync.
    private PoiCollection _collection = null!;
    private HashSet<int> _existingLinks = [];

    private readonly List<NewPoiEntry> _newPois = [];
    private readonly List<PoiCollectionItem> _linksToAdd = [];
    private int _added;
    private int _skipped;

    public async Task<ImportResult> RunAsync()
    {
        await CreateCollectionAsync();
        _existingLinks = await LoadExistingLinksAsync();
        await ProcessItemsAsync();
        await SaveNewPoisAsync();
        AttachImagesToNewPois();
        BuildLinksForNewPois();
        await SaveLinksAndImagesAsync();
        LogCompletion();
        return BuildResult();
    }

    // ---- Phase 1: collection -------------------------------------------------

    private async Task CreateCollectionAsync()
    {
        _collection = new PoiCollection
        {
            Name = collectionName,
            Color = color,
            SourceType = sourceType,
            SourceFileName = sourceFileName,
            CreatedDate = DateTime.UtcNow
        };
        db.PoiCollections.Add(_collection);
        await db.SaveChangesAsync(ct);
    }

    // ---- Phase 2: dedup lookups ----------------------------------------------

    private async Task<HashSet<int>> LoadExistingLinksAsync()
    {
        // The collection was just created in phase 1, so this will always
        // come back empty — we still load it so the dedup path can be
        // written uniformly against a live set.
        return await db.PoiCollectionItems
            .Where(ci => ci.PoiCollectionId == _collection.Id)
            .Select(ci => ci.PoiId)
            .AsAsyncEnumerable()
            .ToHashSetAsync(ct);
    }

    // ---- Phase 3: per-item dispatch ------------------------------------------

    private async Task ProcessItemsAsync()
    {
        foreach (var imported in validParsed)
        {
            ct.ThrowIfCancellationRequested();

            var existing = await FindExistingMatchAsync(imported);
            if (existing != null)
            {
                await HandleDuplicateAsync(existing, imported);
            }
            else
            {
                AddNewPoi(imported);
            }
        }
    }

    private async Task<Poi?> FindExistingMatchAsync(ImportedPoi imported)
    {
        var shell = new Poi
        {
            Name = imported.Name,
            Latitude = imported.Latitude,
            Longitude = imported.Longitude
        };

        var normalizedName = imported.Name.ToLowerInvariant().Trim();
        var existingCandidates = await db.Pois
            .Where(p => p.Name.ToLower() == normalizedName)
            .ToListAsync(ct);

        foreach (var candidate in existingCandidates)
        {
            if (PoiIdentity.AreSamePlace(candidate, shell))
            {
                return candidate;
            }
        }

        return _newPois.Select(x => x.Poi).FirstOrDefault(inBatch => PoiIdentity.AreSamePlace(inBatch, shell));
    }

    // ---- Phase 3a: dedup branch ----------------------------------------------

    private async Task HandleDuplicateAsync(Poi existing, ImportedPoi imported)
    {
        // In-batch duplicate (same name+coords as a Poi we queued earlier
        // this cycle). Its Id is still 0 — the first occurrence's link is
        // created by the newPois pass after SaveChanges, so we just bump
        // the skipped counter and bail.
        if (existing.Id == 0)
        {
            _skipped++;
            return;
        }

        LinkToCollectionIfMissing(existing);
        BackfillGoogleMapsUrl(existing, imported);
        await BackfillImageIfAllowedAsync(existing, imported);
        _skipped++;
    }

    private void LinkToCollectionIfMissing(Poi existing)
    {
        if (_existingLinks.Contains(existing.Id))
        {
            return;
        }

        _linksToAdd.Add(new PoiCollectionItem
        {
            PoiId = existing.Id,
            PoiCollectionId = _collection.Id
        });
        _existingLinks.Add(existing.Id);
    }

    /// <summary>
    /// Upgrades the existing POI's GoogleMapsUrl to a proper /maps/place/ URL
    /// when the new import has one and the existing row does not.
    /// </summary>
    private static void BackfillGoogleMapsUrl(Poi existing, ImportedPoi imported)
    {
        if (string.IsNullOrEmpty(imported.GoogleMapsUrl))
        {
            return;
        }

        var normalizedImported = PoiMatcher.NormalizeUrl(imported.GoogleMapsUrl);
        if (string.IsNullOrWhiteSpace(normalizedImported))
        {
            return;
        }

        if (IsCoordinateSearchFallback(normalizedImported))
        {
            return;
        }

        var normalizedExisting = string.IsNullOrWhiteSpace(existing.GoogleMapsUrl)
            ? null
            : PoiMatcher.NormalizeUrl(existing.GoogleMapsUrl);

        if (string.Equals(normalizedExisting, normalizedImported, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (IsCanonicalPlaceUrl(normalizedExisting))
        {
            return;
        }

        existing.GoogleMapsUrl = normalizedImported;
    }

    private static bool IsCanonicalPlaceUrl(string? url)
    {
        return !string.IsNullOrWhiteSpace(url)
               && url.Contains("/maps/place/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCoordinateSearchFallback(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!uri.Host.Contains("google.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!uri.AbsolutePath.Equals("/maps/search", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = uri.Query;
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        foreach (var segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = segment.Split('=', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            if (!parts[0].Equals("query", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = Uri.UnescapeDataString(parts[1]);
            var coordParts = value.Split(',', 2);
            if (coordParts.Length != 2)
            {
                return false;
            }

            return double.TryParse(coordParts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                   && double.TryParse(coordParts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        }

        return false;
    }

    private async Task BackfillImageIfAllowedAsync(Poi existing, ImportedPoi imported)
    {
        // Narrow backfill policy: we only overwrite an image when the
        // existing record is empty or came from a Google source URL.
        // Non-Google URLs are treated as user-set and left untouched, as
        // are address/rating/phone and other metadata.
        var hasStoredImageBytes = await db.PoiImages
            .AsNoTracking()
            .AnyAsync(i => i.PoiId == existing.Id, ct);

        var existingIsGoogleSourced = string.IsNullOrEmpty(existing.ImageUrl)
                                      || existing.ImageUrl.Contains("googleusercontent.com")
                                      || !hasStoredImageBytes;
        if (!existingIsGoogleSourced)
        {
            return;
        }

        if (imported.ImageData is { Length: > 0 })
        {
            await ReplaceImageBytesAsync(existing, imported);
        }
        else if (!string.IsNullOrEmpty(imported.ImageUrl))
        {
            // URL-only fallback when the bytes download failed.
            existing.ImageUrl = imported.ImageUrl;
        }
    }

    private async Task ReplaceImageBytesAsync(Poi existing, ImportedPoi imported)
    {
        // Find pulls from the change tracker first, so concurrent backfills
        // in the same batch don't duplicate-insert the companion row.
        var existingImage = await db.PoiImages.FindAsync([existing.Id], ct);
        if (existingImage is null)
        {
            db.PoiImages.Add(new PoiImage
            {
                PoiId = existing.Id,
                Data = imported.ImageData!,
                ContentType = imported.ImageContentType
            });
        }
        else
        {
            existingImage.Data = imported.ImageData!;
            existingImage.ContentType = imported.ImageContentType;
        }
        existing.ImageUrl = imported.ImageUrl;
    }

    // ---- Phase 3b: new-POI branch --------------------------------------------

    private void AddNewPoi(ImportedPoi imported)
    {
        var poi = BuildPoi(imported);
        db.Pois.Add(poi);
        _newPois.Add(new NewPoiEntry(poi, imported.ImageData, imported.ImageContentType));

        _added++;
    }

    private Poi BuildPoi(ImportedPoi imported)
    {
        return new Poi
        {
            Name = imported.Name,
            Latitude = imported.Latitude,
            Longitude = imported.Longitude,
            GoogleMapsUrl = !string.IsNullOrEmpty(imported.GoogleMapsUrl)
                ? PoiMatcher.NormalizeUrl(imported.GoogleMapsUrl)
                : null,
            Address = imported.Address,
            Category = imported.Category,
            Notes = imported.Description,
            GoogleRating = imported.Rating,
            ReviewCount = imported.ReviewCount,
            Website = imported.Website,
            Phone = imported.Phone,
            ImageUrl = imported.ImageUrl,
            // PoiImage is attached in a second pass after SaveChanges:
            // EF Core's relationship fixup on a one-to-one with a
            // store-generated principal key fails if we set it here
            // ("PoiImage.PoiId unknown" on save).
            Status = ImportedStatus,
            AddedDate = DateTime.UtcNow,
            // Every imported row goes through enrichment. The BG service
            // fills only empty fields (preserves whatever the file gave us)
            // and adds the place photo + canonical /maps/place URL.
            IsEnriched = false
        };
    }

    // ---- Phase 4: persistence ------------------------------------------------

    private async Task SaveNewPoisAsync()
    {
        // Save in batch so EF populates Ids for phase 5/6.
        if (_newPois.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    private void AttachImagesToNewPois()
    {
        // Second pass — now that Pois have generated Ids, attach image
        // rows with the correct foreign key.
        foreach (var entry in _newPois)
        {
            if (entry.ImageData is { Length: > 0 })
            {
                db.PoiImages.Add(new PoiImage
                {
                    PoiId = entry.Poi.Id,
                    Data = entry.ImageData,
                    ContentType = entry.ImageContentType
                });
            }
        }
    }

    private void BuildLinksForNewPois()
    {
        foreach (var entry in _newPois)
        {
            _linksToAdd.Add(new PoiCollectionItem
            {
                PoiId = entry.Poi.Id,
                PoiCollectionId = _collection.Id
            });
        }
    }

    private async Task SaveLinksAndImagesAsync()
    {
        if (_linksToAdd.Count > 0)
        {
            db.PoiCollectionItems.AddRange(_linksToAdd);
        }

        await db.SaveChangesAsync(ct);
    }

    // ---- Phase 5: reporting --------------------------------------------------

    private void LogCompletion()
    {
        logger.LogInformation(
            "Import complete for collection '{CollectionName}' (ID={CollectionId}): {Added} added, {Skipped} duplicates linked, {Total} total parsed",
            collectionName, _collection.Id, _added, _skipped, totalParsed);
    }

    public bool AddedAny => _added > 0;

    private ImportResult BuildResult()
    {
        return new ImportResult
        {
            AddedCount = _added,
            SkippedCount = _skipped,
            TotalParsed = totalParsed,
            CollectionId = _collection.Id,
            CollectionName = collectionName
        };
    }

    private sealed record NewPoiEntry(Poi Poi, byte[]? ImageData, string? ImageContentType);
}
