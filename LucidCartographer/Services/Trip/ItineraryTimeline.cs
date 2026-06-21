using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services.Trip;

// Service-layer timeline computation with no I/O or side effects.
// Canonical seconds internally; dwell/budget minutes converted to seconds at this edge.

/// <summary>
/// An ordered placeable stop: <see cref="PoiId"/> and dwell in minutes
/// (<c>null</c> contributes zero).
/// </summary>
public readonly record struct ItineraryStopInput(int PoiId, int? DwellMinutes);

/// <summary>
/// One leg between consecutive stops: <see cref="DurationSeconds"/> (canonical travel time,
/// <c>null</c> = unknown) and <see cref="Fidelity"/> (provenance, <c>null</c> = unknown).
/// Either null/Placeholder makes the leg Unknown — timeline never guesses across it.
/// </summary>
public readonly record struct ItineraryLegInput(int? DurationSeconds, string? Fidelity);

/// <summary>
/// One resolved arrival: <see cref="OffsetSeconds"/> (cumulative from trip start, absent if <see cref="IsUnknown"/>),
/// <see cref="ArrivalWallClock"/> (absolute time when trip has a start and arrival is known),
/// <see cref="QualifyingFidelity"/> (lowest-trust leg provenance; <c>null</c> = all-Manual/Measured),
/// and <see cref="IsUnknown"/> (true when upstream leg duration was unknown, arrival uncomputable).
/// </summary>
public sealed record ItineraryArrival(
    int PoiId,
    int? OffsetSeconds,
    DateTime? ArrivalWallClock,
    string? QualifyingFidelity,
    bool IsUnknown);

/// <summary>
/// Immutable timeline result: <see cref="Stops"/> (per-stop arrivals), <see cref="FinishOrReturn"/> (terminal arrival or <c>null</c>),
/// <see cref="TotalSeconds"/> (whole-trip duration including all dwell, <c>null</c> if <see cref="IsTotalUnknown"/>),
/// <see cref="TotalQualifyingFidelity"/>, and <see cref="IsOverBudget"/> (asserted only when budget is set, total known, and exceeded).
/// </summary>
public sealed record ItineraryTimelineResult(
    IReadOnlyList<ItineraryArrival> Stops,
    ItineraryArrival? FinishOrReturn,
    int? TotalSeconds,
    string? TotalQualifyingFidelity,
    bool IsTotalUnknown,
    bool IsOverBudget)
{
    /// <summary>Empty timeline (fewer than two placeable stops).</summary>
    public static readonly ItineraryTimelineResult Empty =
        new([], null, null, null, IsTotalUnknown: false, IsOverBudget: false);
}

/// <summary>Itinerary timeline computation.</summary>
public static class ItineraryTimeline
{
    // Fidelity rank: Unknown (null or Placeholder) = 0, Estimated = 1, Manual/Measured = 2 (confident, no qualifier).
    // A cumulative arrival's qualifier = LOWEST rank among legs summed so far. Rank 0 anywhere ⇒ unknown.
    private const int RankUnknown = 0;
    private const int RankEstimated = 1;
    private const int RankConfident = 2;

    /// <summary>
    /// Walks ordered stops and legs into a timeline.
    /// </summary>
    /// <param name="stops">Ordered stops (PoiId + dwell minutes).</param>
    /// <param name="legs">
    /// Ordered legs between stops, then (Roundtrip) the closing leg back to Start.
    /// Open path: N−1 legs. Roundtrip: N legs.
    /// </param>
    /// <param name="unplaceableDwellMinutes">
    /// Dwell of every unplaceable stop; contributes only to <see cref="ItineraryTimelineResult.TotalSeconds"/>.
    /// </param>
    /// <param name="isRoundtrip">
    /// True ⇒ the closing leg returns to Start, yielding a distinct return-to-Start
    /// arrival. False ⇒ open path, ending at the Finish (no return entry).
    /// </param>
    /// <param name="tripStart">The wall-clock start time, or null ⇒ relative offsets only.</param>
    /// <param name="budgetMinutes">The soft time budget in minutes, or null ⇒ no overrun flag ever.</param>
    public static ItineraryTimelineResult Compute(
        IReadOnlyList<ItineraryStopInput> stops,
        IReadOnlyList<ItineraryLegInput> legs,
        IReadOnlyList<int?> unplaceableDwellMinutes,
        bool isRoundtrip,
        DateTime? tripStart,
        int? budgetMinutes)
    {
        ArgumentNullException.ThrowIfNull(stops);
        ArgumentNullException.ThrowIfNull(legs);
        ArgumentNullException.ThrowIfNull(unplaceableDwellMinutes);

        if (stops.Count < 2)
        {
            return ItineraryTimelineResult.Empty;
        }

        var arrivals = new List<ItineraryArrival>(stops.Count);

        // Cumulative travel+dwell offset from trip start in seconds; first arrival = offset 0.
        var cumulativeSeconds = 0;

        // Running minimum fidelity rank; Unknown leg (rank 0) taints all downstream arrivals.
        var runningRank = RankConfident;

        // True once any Unknown leg is encountered.
        var unknownFromHere = false;

        arrivals.Add(MakeArrival(stops[0].PoiId, cumulativeSeconds, tripStart, runningRank, isUnknown: false));

        for (var k = 0; k < stops.Count - 1; k++)
        {
            cumulativeSeconds += DwellSeconds(stops[k].DwellMinutes);

            var leg = legs[k];
            runningRank = Math.Min(runningRank, RankOf(leg));
            if (RankOf(leg) == RankUnknown || leg.DurationSeconds is not { } travel)
            {
                unknownFromHere = true;
            }
            else
            {
                // Round-once: accumulate DisplayMinutes (×60), not raw seconds, to avoid stray seconds in cumulative arrivals.
                cumulativeSeconds += TravelTimeFormatting.DisplayMinutes(travel) * 60;
            }

            arrivals.Add(unknownFromHere
                ? UnknownArrival(stops[k + 1].PoiId)
                : MakeArrival(stops[k + 1].PoiId, cumulativeSeconds, tripStart, runningRank, isUnknown: false));
        }

        ItineraryArrival? finishOrReturn;
        if (isRoundtrip)
        {
            // Roundtrip: dwell at final stop, then closing leg back to Start.
            cumulativeSeconds += DwellSeconds(stops[^1].DwellMinutes);
            var closing = legs[^1];
            runningRank = Math.Min(runningRank, RankOf(closing));
            if (RankOf(closing) == RankUnknown || closing.DurationSeconds is not { } travel)
            {
                unknownFromHere = true;
            }
            else
            {
                // Round-once: same accumulation for closing leg.
                cumulativeSeconds += TravelTimeFormatting.DisplayMinutes(travel) * 60;
            }

            finishOrReturn = unknownFromHere
                ? UnknownArrival(stops[0].PoiId)
                : MakeArrival(stops[0].PoiId, cumulativeSeconds, tripStart, runningRank, isUnknown: false);
        }
        else
        {
            // Open path: terminal entry is already in arrivals; mirror as finish/return.
            finishOrReturn = arrivals[^1];
        }

        // Total includes all dwell (routed + unplaceable). Unknown total iff terminal entry is unknown.
        var isTotalUnknown = finishOrReturn?.IsUnknown ?? unknownFromHere;
        int? totalSeconds;
        string? totalQualifier;
        if (isTotalUnknown)
        {
            totalSeconds = null;
            totalQualifier = null;
        }
        else
        {
            var total = cumulativeSeconds;
            foreach (var dwell in unplaceableDwellMinutes)
            {
                total += DwellSeconds(dwell);
            }

            totalSeconds = total;
            totalQualifier = QualifierFor(runningRank);
        }

        // Overrun only when budget is set, total is known, and exceeded.
        var isOverBudget = budgetMinutes is { } budget
            && totalSeconds is { } known
            && known > budget * 60;

        return new ItineraryTimelineResult(
            arrivals,
            finishOrReturn,
            totalSeconds,
            totalQualifier,
            isTotalUnknown,
            isOverBudget);
    }

    private static ItineraryArrival MakeArrival(
        int poiId, int offsetSeconds, DateTime? tripStart, int runningRank, bool isUnknown) =>
        new(
            poiId,
            OffsetSeconds: offsetSeconds,
            ArrivalWallClock: tripStart is { } start ? start.AddSeconds(offsetSeconds) : null,
            QualifyingFidelity: QualifierFor(runningRank),
            IsUnknown: isUnknown);

    private static ItineraryArrival UnknownArrival(int poiId) =>
        new(poiId, OffsetSeconds: null, ArrivalWallClock: null, QualifyingFidelity: null, IsUnknown: true);

    // Fidelity rank of a leg: Unknown (null or Placeholder) = 0, Estimated = 1, Manual/Measured = 2.
    private static int RankOf(ItineraryLegInput leg)
    {
        if (leg.DurationSeconds is null)
        {
            return RankUnknown;
        }

        return leg.Fidelity switch
        {
            Fidelity.Measured or Fidelity.Manual => RankConfident,
            Fidelity.Estimated => RankEstimated,
            // Placeholder, null, or unrecognized ⇒ Unknown.
            _ => RankUnknown,
        };
    }

    // Estimated rank ⇒ Estimated qualifier; all-confident ⇒ no qualifier.
    private static string? QualifierFor(int rank) =>
        rank == RankEstimated ? Fidelity.Estimated : null;

    // Convert dwell minutes to seconds; null or negative dwell = zero (no stay).
    private static int DwellSeconds(int? minutes) =>
        minutes is { } m && m > 0 ? m * 60 : 0;
}
