using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;

namespace LucidCartographer.Services.Trip;

/// <summary>
/// TRIP-TRAVELTIME-01: off-circuit (re)computation of per-leg travel time (AR-5),
/// mirroring <c>PoiEnrichmentBackgroundService</c>. The loop blocks on
/// <see cref="TravelTimeTrigger.WaitAsync"/>; on each wake it loads every
/// Trip-View-enabled collection's ordered, placeable stops, forms the directional
/// leg pairs (consecutive k→k+1, plus the closing leg back to the Start on a
/// Roundtrip — the same shape as <c>TripViewModel.BuildLegs</c>), calls the active
/// provider through the Polly "travel-time" pipeline for each pair lacking a cache
/// row, and upserts the result into <see cref="RouteSegment"/> under the shared
/// <see cref="SqliteWriteLock"/>.
///
/// SCOPE: write-on-compute + read-back only. Cache invalidation / recompute on
/// coord/mode/provider change (Story 2.4) is OUT of scope — a leg is computed iff
/// no cache row exists yet for its (FromPoiId, ToPoiId, TravelMode) key.
/// </summary>
public sealed class TravelTimeComputationBackgroundService(
    IDbContextFactory<AppDbContext> factory,
    TravelTimeTrigger trigger,
    TravelTimeProgressService progress,
    ITravelTimeProvider provider,
    SqliteWriteLock writeLock,
    ResiliencePipelineProvider<string> pipelineProvider,
    IOptions<TravelTimeOptions> options,
    ILogger<TravelTimeComputationBackgroundService> logger) : BackgroundService
{
    private readonly ResiliencePipeline _pipeline = pipelineProvider.GetPipeline("travel-time");
    private readonly TimeSpan _idlePoll =
        TimeSpan.FromSeconds(Math.Max(1, options.Value.IdlePollSeconds));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("TravelTimeComputationBackgroundService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(stoppingToken);
                // Sleep until the idle timeout fires OR the VM signals that a
                // Trip turned on / projections rebuilt with missing cache rows.
                await trigger.WaitAsync(_idlePoll, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Travel-time compute cycle failed; sleeping before retry");
                try { await Task.Delay(_idlePoll, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        logger.LogInformation("TravelTimeComputationBackgroundService stopping");
    }

    /// <summary>
    /// One full pass: compute and upsert every missing leg across all
    /// Trip-View-enabled collections. Internal so tests can drive it directly
    /// (InternalsVisibleTo) without standing up the hosted loop.
    /// </summary>
    internal async Task ProcessOnceAsync(CancellationToken ct)
    {
        var legs = await LoadPendingLegsAsync(ct);
        progress.Set(legs.Count);
        if (legs.Count == 0)
        {
            return;
        }

        var remaining = legs.Count;
        foreach (var leg in legs)
        {
            ct.ThrowIfCancellationRequested();

            TravelLegResult result;
            try
            {
                result = await _pipeline.ExecuteAsync(
                    async innerCt => await provider.GetLegAsync(leg.From, leg.To, leg.TravelMode, innerCt),
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Provider-down fallback + failure-copy is Story 2.3; here we just
                // log and leave the leg uncomputed so a later cycle retries it.
                logger.LogWarning(ex,
                    "Travel-time provider failed for leg {From}->{To} ({Mode}); leaving uncomputed",
                    leg.From.PoiId, leg.To.PoiId, leg.TravelMode);
                remaining--;
                progress.Set(remaining);
                continue;
            }

            await UpsertAsync(leg, result, ct);
            remaining--;
            progress.Set(remaining);
        }
    }

    /// <summary>
    /// Reads every Trip-View-enabled collection's ordered, placeable stops and
    /// returns the directional leg pairs that have no <see cref="RouteSegment"/>
    /// cache row yet under the collection's persisted <see cref="TravelMode"/>.
    /// </summary>
    private async Task<List<PendingLeg>> LoadPendingLegsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var collections = await db.PoiCollections
            .AsNoTracking()
            .Where(c => c.TripViewEnabled)
            .Select(c => new { c.Id, c.TravelMode, c.StartPoiId, c.FinishPoiId })
            .ToListAsync(ct);

        if (collections.Count == 0)
        {
            return [];
        }

        // Existing cache keys so we never recompute a leg that already has a row
        // (invalidation is Story 2.4). Tuple set keyed (From, To, Mode). This
        // already covers TRIP-MANUAL-01 (Story 2.2): a manually-entered Any/Air
        // leg is just a RouteSegment row, so its key is "present" here and the leg
        // is never re-queued — the user's manual time is preserved.
        var existing = await db.RouteSegments
            .AsNoTracking()
            .Select(r => new { r.FromPoiId, r.ToPoiId, r.TravelMode })
            .ToListAsync(ct);
        var have = existing
            .Select(r => (r.FromPoiId, r.ToPoiId, r.TravelMode))
            .ToHashSet();

        var pending = new List<PendingLeg>();
        var seen = new HashSet<(int, int, string)>();

        foreach (var c in collections)
        {
            var members = await db.PoiCollectionItems
                .AsNoTracking()
                .Where(ci => ci.PoiCollectionId == c.Id)
                .Select(ci => new { ci.PoiId, ci.OrderIndex, ci.Poi.Latitude, ci.Poi.Longitude })
                .ToListAsync(ct);

            var stops = members
                .Where(m => m.OrderIndex > 0 && StopPlaceability.IsPlaceable(m.Latitude, m.Longitude))
                .OrderBy(m => m.OrderIndex)
                .Select(m => new TravelEndpoint(m.PoiId, m.Latitude!.Value, m.Longitude!.Value))
                .ToList();

            foreach (var (from, to) in DirectionalPairs(stops, c.FinishPoiId))
            {
                var key = (from.PoiId, to.PoiId, c.TravelMode);
                if (have.Contains(key) || !seen.Add(key))
                {
                    continue;
                }

                pending.Add(new PendingLeg(from, to, c.TravelMode));
            }
        }

        return pending;
    }

    /// <summary>
    /// The directional leg pairs for an ordered stop list, mirroring
    /// <c>TripViewModel.BuildLegs</c>: consecutive k→k+1, plus the closing leg
    /// from the last stop back to the first on a Roundtrip (no distinct Finish).
    /// </summary>
    private static IEnumerable<(TravelEndpoint From, TravelEndpoint To)> DirectionalPairs(
        IReadOnlyList<TravelEndpoint> stops, int? finishPoiId)
    {
        if (stops.Count < 2)
        {
            yield break;
        }

        for (var k = 0; k < stops.Count - 1; k++)
        {
            yield return (stops[k], stops[k + 1]);
        }

        var finishIsDistinctStop = finishPoiId is { } fid
            && fid != stops[0].PoiId
            && stops.Any(s => s.PoiId == fid);
        if (!finishIsDistinctStop)
        {
            yield return (stops[^1], stops[0]);
        }
    }

    /// <summary>
    /// Upserts one computed leg into the cache under the write lock. Idempotent:
    /// an existing row for the key is updated in place (no duplicate inserted).
    /// </summary>
    private async Task UpsertAsync(PendingLeg leg, TravelLegResult result, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var existing = await db.RouteSegments.FirstOrDefaultAsync(
            r => r.FromPoiId == leg.From.PoiId
                 && r.ToPoiId == leg.To.PoiId
                 && r.TravelMode == leg.TravelMode,
            ct);

        // TRIP-MANUAL-01 (Story 2.2, AC6): never overwrite a user's manual entry.
        // LoadPendingLegsAsync already skips any pair that has a row, so a compute
        // pass should not reach here for a Manual leg — but this explicit guard
        // makes the protection defensive against a future recompute path (Story
        // 2.4) that might re-queue an existing key. A Manual row is changed/cleared
        // only by the user (TripViewModel.Set/ClearManualLegTimeAsync).
        if (existing is not null && existing.Fidelity == Fidelity.Manual)
        {
            return;
        }

        if (existing is null)
        {
            db.RouteSegments.Add(new RouteSegment
            {
                FromPoiId = leg.From.PoiId,
                ToPoiId = leg.To.PoiId,
                TravelMode = leg.TravelMode,
                DurationSeconds = result.DurationSeconds,
                DistanceMeters = result.DistanceMeters,
                GeometryPolyline = result.GeometryPolyline,
                Fidelity = result.Fidelity,
                Source = provider.Source,
                ComputedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.DurationSeconds = result.DurationSeconds;
            existing.DistanceMeters = result.DistanceMeters;
            existing.GeometryPolyline = result.GeometryPolyline;
            existing.Fidelity = result.Fidelity;
            existing.Source = provider.Source;
            existing.ComputedAt = DateTime.UtcNow;
        }

        await writeLock.Gate.WaitAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            writeLock.Gate.Release();
        }
    }

    private readonly record struct PendingLeg(TravelEndpoint From, TravelEndpoint To, string TravelMode);
}
