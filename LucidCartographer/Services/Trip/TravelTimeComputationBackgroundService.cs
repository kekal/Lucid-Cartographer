using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;

namespace LucidCartographer.Services.Trip;

/// <summary>
/// Off-circuit (re)computation of per-leg travel time, mirroring <c>PoiEnrichmentBackgroundService</c>.
/// Loads Trip-View-enabled collections' ordered placeable stops, forms directional leg pairs
/// (k→k+1, plus closing leg back to start on Roundtrip), calls the provider for uncached pairs,
/// and upserts results into <see cref="RouteSegment"/> under <see cref="SqliteWriteLock"/>.
/// Computes when no cache row exists for the (FromPoiId, ToPoiId, TravelMode) key, or when a
/// measured-capable provider can upgrade an existing low-fidelity Estimated/Placeholder row
/// produced from Mock/EstimatedFallback (capability-gated recompute, AD-2).
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
            string source;
            try
            {
                result = await _pipeline.ExecuteAsync(
                    async innerCt => await provider.GetLegAsync(leg.From, leg.To, leg.TravelMode, innerCt),
                    ct);
                source = provider.Source;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Provider failed (unreachable/no route): fall back to haversine estimate instead of failing the loop.
                result = EstimatedTravelTime.Compute(leg.From, leg.To, leg.TravelMode, options.Value);
                source = TravelTimeSource.EstimatedFallback;
                logger.LogWarning(ex,
                    "Travel-time provider failed for leg {From}->{To} ({Mode}); degraded to {Fidelity} via straight-line fallback",
                    leg.From.PoiId, leg.To.PoiId, leg.TravelMode, result.Fidelity);
            }

            await UpsertAsync(leg, result, source, ct);
            remaining--;
            progress.Set(remaining);
        }
    }

    /// <summary>
    /// Reads Trip-View-enabled collections' ordered placeable stops and returns directional leg pairs
    /// that travel under GROUND mode (Walk/Drive/Cycle) and lack a cache row. Each leg uses the
    /// From-stop's <c>OutgoingTravelMode</c> (null = AnyAir), not the collection's trip-wide mode.
    /// AnyAir legs are never enqueued or auto-estimated.
    /// </summary>
    private async Task<List<PendingLeg>> LoadPendingLegsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var collections = await db.PoiCollections
            .AsNoTracking()
            .Where(c => c.TripViewEnabled)
            .Select(c => new { c.Id, c.StartPoiId, c.FinishPoiId })
            .ToListAsync(ct);

        if (collections.Count == 0)
        {
            return [];
        }

        // Existing cache rows, projected with Fidelity + Source so upgrade-eligibility can be
        // evaluated per leg. A row keyed by (From, To, Mode) makes the leg non-pending UNLESS
        // a measured-capable provider can upgrade it (AD-2, below). Manually-entered and
        // already-Measured legs are just RouteSegment rows here and are never upgrade-eligible.
        var existing = await db.RouteSegments
            .AsNoTracking()
            .Select(r => new { r.FromPoiId, r.ToPoiId, r.TravelMode, r.Fidelity, r.Source })
            .ToListAsync(ct);
        var cached = new Dictionary<(int, int, string), (string Fidelity, string Source)>();
        foreach (var r in existing)
        {
            cached[(r.FromPoiId, r.ToPoiId, r.TravelMode)] = (r.Fidelity, r.Source);
        }

        // Capability gate (Story 2.1 seam): only a measured-capable provider (Valhalla=true)
        // re-enqueues upgrade-eligible rows. Mock=false collapses to the legacy "no row" rule,
        // so a Mock deployment never re-churns its own Estimated rows into an infinite loop.
        var measuredCapable = provider.ProducesMeasuredFidelity;

        var pending = new List<PendingLeg>();
        var seen = new HashSet<(int, int, string)>();

        foreach (var c in collections)
        {
            var members = await db.PoiCollectionItems
                .AsNoTracking()
                .Where(ci => ci.PoiCollectionId == c.Id)
                .Select(ci => new { ci.PoiId, ci.OrderIndex, ci.OutgoingTravelMode, ci.Poi.Latitude, ci.Poi.Longitude })
                .ToListAsync(ct);

            // Carry each From-stop's outgoing mode (null = AnyAir) to drive enqueue and cache key.
            var stops = members
                .Where(m => m.OrderIndex > 0 && StopPlaceability.IsPlaceable(m.Latitude, m.Longitude))
                .OrderBy(m => m.OrderIndex)
                .Select(m => new PendingStop(
                    new TravelEndpoint(m.PoiId, m.Latitude!.Value, m.Longitude!.Value),
                    m.OutgoingTravelMode ?? TravelMode.AnyAir))
                .ToList();

            foreach (var (from, to, mode) in DirectionalPairs(stops, c.FinishPoiId))
            {
                // Only GROUND legs (Walk/Drive/Cycle) auto-compute; AnyAir legs are never enqueued.
                if (!IsGroundMode(mode))
                {
                    continue;
                }

                var key = (from.PoiId, to.PoiId, mode);
                if (!seen.Add(key))
                {
                    continue; // already queued this pass — dedupe regardless of cache state.
                }

                if (cached.TryGetValue(key, out var row)
                    && !(measuredCapable && IsUpgradeEligible(row.Fidelity, row.Source)))
                {
                    // A row exists and is NOT a measured-upgradeable estimate ⇒ leave it alone.
                    // When measuredCapable is false this is byte-for-byte the legacy "any row ⇒ skip".
                    continue;
                }

                pending.Add(new PendingLeg(from, to, mode));
            }
        }

        return pending;
    }

    /// <summary>
    /// A cached row is eligible for measured upgrade iff it is a low-fidelity, self-produced
    /// estimate — an Estimated/Placeholder row from Mock/EstimatedFallback. Manual/Measured
    /// rows (and estimates from any other source) are never upgrade-eligible (AD-2).
    /// </summary>
    private static bool IsUpgradeEligible(string fidelity, string source) =>
        (fidelity is Fidelity.Estimated or Fidelity.Placeholder)
        && (source is TravelTimeSource.Mock or TravelTimeSource.EstimatedFallback);

    /// <summary>True for ground modes that auto-compute (Walk/Drive/Cycle); AnyAir is excluded.</summary>
    private static bool IsGroundMode(string mode) =>
        mode is TravelMode.Walk or TravelMode.Drive or TravelMode.Cycle;

    /// <summary>
    /// Directional leg pairs for an ordered stop list: consecutive k→k+1, plus closing leg
    /// from last stop back to first on Roundtrip (no distinct Finish). Mirrors <c>TripViewModel.BuildLegs</c>.
    /// </summary>
    private static IEnumerable<(TravelEndpoint From, TravelEndpoint To, string Mode)> DirectionalPairs(
        IReadOnlyList<PendingStop> stops, int? finishPoiId)
    {
        if (stops.Count < 2)
        {
            yield break;
        }

        for (var k = 0; k < stops.Count - 1; k++)
        {
            yield return (stops[k].Endpoint, stops[k + 1].Endpoint, stops[k].OutgoingTravelMode);
        }

        var finishIsDistinctStop = finishPoiId is { } fid
            && fid != stops[0].Endpoint.PoiId
            && stops.Any(s => s.Endpoint.PoiId == fid);
        if (!finishIsDistinctStop)
        {
            // Closing leg uses last stop's outgoing mode.
            yield return (stops[^1].Endpoint, stops[0].Endpoint, stops[^1].OutgoingTravelMode);
        }
    }

    /// <summary>An ordered placeable stop plus its From-leg mode (null normalized to AnyAir).</summary>
    private readonly record struct PendingStop(TravelEndpoint Endpoint, string OutgoingTravelMode);

    /// <summary>
    /// Upserts one computed leg into the cache under the write lock. Idempotent: existing rows
    /// are updated in place. Internal so the Manual/Measured no-downgrade guard can be tested directly.
    /// </summary>
    internal async Task UpsertAsync(PendingLeg leg, TravelLegResult result, string source, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var existing = await db.RouteSegments.FirstOrDefaultAsync(
            r => r.FromPoiId == leg.From.PoiId
                 && r.ToPoiId == leg.To.PoiId
                 && r.TravelMode == leg.TravelMode,
            ct);

        // Never overwrite Manual or Measured rows (user-entered or higher-fidelity data).
        if (existing is not null && existing.Fidelity is Fidelity.Manual or Fidelity.Measured)
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
                Source = source,
                ComputedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.DurationSeconds = result.DurationSeconds;
            existing.DistanceMeters = result.DistanceMeters;
            existing.GeometryPolyline = result.GeometryPolyline;
            existing.Fidelity = result.Fidelity;
            existing.Source = source;
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

    internal readonly record struct PendingLeg(TravelEndpoint From, TravelEndpoint To, string TravelMode);
}
