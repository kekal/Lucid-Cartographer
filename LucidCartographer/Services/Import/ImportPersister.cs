using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Operations;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Services.Import
{
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
    internal sealed class ImportPersister
    {
        private const string ImportedStatus = "imported";
        private const string GoogleScrapeSourceType = "google_maps_scrape";

        private readonly AppDbContext _db;
        private readonly ILogger _logger;
        private readonly IReadOnlyList<ImportedPoi> _validParsed;
        private readonly int _totalParsed;
        private readonly string _collectionName;
        private readonly string _color;
        private readonly string _sourceType;
        private readonly string? _sourceFileName;
        private readonly CancellationToken _ct;

        // State built up during RunAsync.
        private PoiCollection _collection = null!;
        // Candidate pool scanned by PoiIdentity.AreSamePlace for each
        // incoming row. Seeded with existing DB rows that share a name,
        // then augmented in-place as we add new rows so later items in
        // the same batch dedup against items queued earlier.
        private List<Poi> _candidatePool = new();
        private HashSet<int> _existingLinks = new();

        private readonly List<NewPoiEntry> _newPois = new();
        private readonly List<PoiCollectionItem> _linksToAdd = new();
        private int _added;
        private int _skipped;

        public ImportPersister(
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
            _db = db;
            _logger = logger;
            _validParsed = validParsed;
            _totalParsed = totalParsed;
            _collectionName = collectionName;
            _color = color;
            _sourceType = sourceType;
            _sourceFileName = sourceFileName;
            _ct = ct;
        }

        public async Task<ImportResult> RunAsync()
        {
            await CreateCollectionAsync();
            await LoadDedupLookupsAsync();
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
                Name = _collectionName,
                Color = _color,
                SourceType = _sourceType,
                SourceFileName = _sourceFileName,
                CreatedDate = DateTime.UtcNow
            };
            _db.PoiCollections.Add(_collection);
            await _db.SaveChangesAsync(_ct);
        }

        // ---- Phase 2: dedup lookups ----------------------------------------------

        private async Task LoadDedupLookupsAsync()
        {
            _candidatePool = await LoadCandidatePoolAsync();
            _existingLinks = await LoadExistingLinksAsync();
        }

        private async Task<List<Poi>> LoadCandidatePoolAsync()
        {
            // Pull every existing row whose lowercased name matches any
            // name being imported. PoiIdentity.AreSamePlace does the real
            // matching work in memory — we only need the DB to narrow the
            // candidate pool from "all POIs" to "POIs with a plausibly
            // similar name", which SQLite can do cheaply on an indexed
            // LOWER(Name) comparison.
            //
            // Note: this is an exact-lowercase pre-filter. Candidates that
            // pass the PoiMatcher.NameSimilarity threshold but have
            // different lowercased names (e.g., "Cafe Rio" vs "Café Rio"
            // after Unicode normalisation) would be missed here. The
            // existing behaviour already had this limitation; strengthening
            // the pre-filter is a separate concern.
            var importedNames = _validParsed
                .Select(p => p.Name.ToLowerInvariant().Trim())
                .Distinct()
                .ToList();

            if (importedNames.Count == 0)
                return new List<Poi>();

            return await _db.Pois
                .Where(p => importedNames.Contains(p.Name.ToLower()))
                .ToListAsync(_ct);
        }

        private async Task<HashSet<int>> LoadExistingLinksAsync()
        {
            // The collection was just created in phase 1, so this will always
            // come back empty — we still load it so the dedup path can be
            // written uniformly against a live set.
            return await _db.PoiCollectionItems
                .Where(ci => ci.PoiCollectionId == _collection.Id)
                .Select(ci => ci.PoiId)
                .AsAsyncEnumerable()
                .ToHashSetAsync(_ct);
        }

        // ---- Phase 3: per-item dispatch ------------------------------------------

        private async Task ProcessItemsAsync()
        {
            foreach (var imported in _validParsed)
            {
                _ct.ThrowIfCancellationRequested();

                var existing = FindExistingMatch(imported);
                if (existing != null)
                    await HandleDuplicateAsync(existing, imported);
                else
                    AddNewPoi(imported);
            }
        }

        private Poi? FindExistingMatch(ImportedPoi imported)
        {
            // Delegate identity to the single PoiIdentity rule: name
            // similarity + geographic proximity, real coords required.
            // Construct a transient Poi shell for the incoming row so the
            // rule can use the typed overload — no allocation outside the
            // match path, and the shell is discarded immediately.
            var shell = new Poi
            {
                Name = imported.Name,
                Latitude = imported.Latitude,
                Longitude = imported.Longitude
            };

            foreach (var candidate in _candidatePool)
            {
                if (PoiIdentity.AreSamePlace(candidate, shell))
                    return candidate;
            }

            return null;
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
            await BackfillImageIfAllowedAsync(existing, imported);
            _skipped++;
        }

        private void LinkToCollectionIfMissing(Poi existing)
        {
            if (_existingLinks.Contains(existing.Id)) return;

            _linksToAdd.Add(new PoiCollectionItem
            {
                PoiId = existing.Id,
                PoiCollectionId = _collection.Id
            });
            _existingLinks.Add(existing.Id);
        }

        private async Task BackfillImageIfAllowedAsync(Poi existing, ImportedPoi imported)
        {
            // Narrow backfill policy: we only overwrite an image when the
            // existing record is empty or came from a Google source URL.
            // Non-Google URLs are treated as user-set and left untouched, as
            // are address/rating/phone and other metadata.
            var existingIsGoogleSourced = string.IsNullOrEmpty(existing.ImageUrl)
                || existing.ImageUrl.Contains("googleusercontent.com");
            if (!existingIsGoogleSourced) return;

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
            var existingImage = await _db.PoiImages.FindAsync(new object[] { existing.Id }, _ct);
            if (existingImage is null)
            {
                _db.PoiImages.Add(new PoiImage
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
            _db.Pois.Add(poi);
            _newPois.Add(new NewPoiEntry(poi, imported.ImageData, imported.ImageContentType));

            // Extend the candidate pool so subsequent items in this same
            // batch can dedup against rows we just queued. The row still
            // has Id=0 until SaveChangesAsync — HandleDuplicateAsync has
            // an explicit in-batch branch for that case.
            _candidatePool.Add(poi);

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
                // File imports carry whatever address/phone/website the source
                // file provided — skip the enrichment queue. Google-scraped
                // rows only have list-card data, so they start unenriched and
                // the background service fills the rest.
                IsEnriched = _sourceType != GoogleScrapeSourceType
            };
        }

        // ---- Phase 4: persistence ------------------------------------------------

        private async Task SaveNewPoisAsync()
        {
            // Save in batch so EF populates Ids for phase 5/6.
            if (_newPois.Count > 0)
                await _db.SaveChangesAsync(_ct);
        }

        private void AttachImagesToNewPois()
        {
            // Second pass — now that Pois have generated Ids, attach image
            // rows with the correct foreign key.
            foreach (var entry in _newPois)
            {
                if (entry.ImageData is { Length: > 0 })
                {
                    _db.PoiImages.Add(new PoiImage
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
                _db.PoiCollectionItems.AddRange(_linksToAdd);

            await _db.SaveChangesAsync(_ct);
        }

        // ---- Phase 5: reporting --------------------------------------------------

        private void LogCompletion()
        {
            _logger.LogInformation(
                "Import complete for collection '{CollectionName}' (ID={CollectionId}): {Added} added, {Skipped} duplicates linked, {Total} total parsed",
                _collectionName, _collection.Id, _added, _skipped, _totalParsed);
        }

        public bool AddedAny => _added > 0;

        private ImportResult BuildResult()
        {
            return new ImportResult
            {
                AddedCount = _added,
                SkippedCount = _skipped,
                TotalParsed = _totalParsed,
                CollectionId = _collection.Id,
                CollectionName = _collectionName
            };
        }

        private sealed record NewPoiEntry(Poi Poi, byte[]? ImageData, string? ImageContentType);
    }
}
