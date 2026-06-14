using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services.Trip;

// TRIP-TIMELINE-01 (Story 2.6): the honest itinerary timeline. A PURE Service-layer
// function — no DB, no I/O, no state — so the honesty rule (never fake precision)
// can be proven exhaustively in plain unit tests. Canonical seconds internally;
// dwell/budget minutes are converted to seconds only at this computation edge (AR-11).

/// <summary>
/// TRIP-TIMELINE-01: an ordered placeable stop fed into the timeline walk —
/// its <see cref="PoiId"/> and per-membership dwell in minutes (<c>null</c> = unset,
/// contributes zero). The walk visits these in Stop Order.
/// </summary>
public readonly record struct ItineraryStopInput(int PoiId, int? DwellMinutes);

/// <summary>
/// TRIP-TIMELINE-01: one leg between consecutive placeable stops (or the closing
/// leg back to Start on a Roundtrip), in Stop Order. <see cref="DurationSeconds"/>
/// is the canonical travel time (<c>null</c> ⇒ unknown — a 2.2 Placeholder leg
/// already presents a null display duration). <see cref="Fidelity"/> is the leg's
/// provenance (<c>null</c> ⇒ unknown). Either being null/Placeholder makes the leg
/// Unknown (rank 0) — the timeline never guesses across it.
/// </summary>
public readonly record struct ItineraryLegInput(int? DurationSeconds, string? Fidelity);

/// <summary>
/// TRIP-TIMELINE-01: one resolved arrival in the timeline — a routed stop, or the
/// finish/return entry. <see cref="OffsetSeconds"/> is the cumulative relative
/// offset from the trip start (always present unless <see cref="IsUnknown"/>);
/// <see cref="ArrivalWallClock"/> is the absolute clock time, present only when the
/// trip has a start time AND the arrival is known. <see cref="QualifyingFidelity"/>
/// is the lowest-trust qualifier among the legs summed up to here (<c>null</c> ⇒
/// clean confident time, i.e. all-Manual/Measured). <see cref="IsUnknown"/> ⇒ an
/// upstream leg's duration was unknown, so the arrival is genuinely uncomputable
/// (rendered "—") and no offset/wall-clock is given.
/// </summary>
public sealed record ItineraryArrival(
    int PoiId,
    int? OffsetSeconds,
    DateTime? ArrivalWallClock,
    string? QualifyingFidelity,
    bool IsUnknown);

/// <summary>
/// TRIP-TIMELINE-01: the immutable result of the timeline walk.
/// <see cref="Stops"/> are the per-stop arrivals in Stop Order; <see cref="FinishOrReturn"/>
/// is the trip's terminal arrival (the return-to-Start arrival on a Roundtrip, or the
/// Finish arrival on an open path), or <c>null</c> when there is no terminal entry
/// (fewer than two placeable stops). <see cref="TotalSeconds"/> is the whole-trip
/// duration (travel + every dwell, including unplaceable dwell) — <c>null</c> when
/// <see cref="IsTotalUnknown"/>. <see cref="IsOverBudget"/> is asserted ONLY when a
/// budget is set AND the total is known AND exceeds it — never a false overrun.
/// </summary>
public sealed record ItineraryTimelineResult(
    IReadOnlyList<ItineraryArrival> Stops,
    ItineraryArrival? FinishOrReturn,
    int? TotalSeconds,
    string? TotalQualifyingFidelity,
    bool IsTotalUnknown,
    bool IsOverBudget)
{
    /// <summary>An empty timeline (Trip View off, or fewer than two placeable stops).</summary>
    public static readonly ItineraryTimelineResult Empty =
        new([], null, null, null, IsTotalUnknown: false, IsOverBudget: false);
}

/// <summary>
/// TRIP-TIMELINE-01 (Story 2.6): the honest itinerary timeline computation.
/// </summary>
public static class ItineraryTimeline
{
    // Fidelity rank (least→most trusted). Unknown (Placeholder OR null duration) = 0,
    // Estimated = 1, Manual = 2, Measured = 2. Manual and Measured are both "confident":
    // neither adds a qualifier. A cumulative arrival's qualifier = the LOWEST rank among
    // all legs summed up to it. Rank 0 anywhere upstream ⇒ unknown (never guess).
    private const int RankUnknown = 0;
    private const int RankEstimated = 1;
    private const int RankConfident = 2;

    /// <summary>
    /// Walks the ordered placeable stops + legs into an honest timeline.
    /// </summary>
    /// <param name="stops">Ordered placeable stops (PoiId + dwell minutes), in Stop Order.</param>
    /// <param name="legs">
    /// Ordered legs between consecutive stops, then (on a Roundtrip) the closing leg back
    /// to Start. Open path: N−1 legs (last stop is the Finish, no return). Roundtrip: N legs.
    /// </param>
    /// <param name="unplaceableDwellMinutes">
    /// The dwell (minutes) of every unplaceable stop. Each contributes ONLY to
    /// <see cref="ItineraryTimelineResult.TotalSeconds"/> — an unplaceable stop has no
    /// leg (no travel time) and no per-stop arrival in the routed sequence (it is not on
    /// the route). This is the documented unplaceable-dwell interpretation (AC4).
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

        // Cumulative travel+dwell offset, in seconds, from the trip start. The first
        // arrival is offset 0 (= tripStart); the Start's dwell is counted once at the
        // beginning (it pushes the DEPARTURE from Start, not its arrival).
        var cumulativeSeconds = 0;

        // The running minimum fidelity rank across the legs summed so far. Starts at
        // the most-trusted sentinel; each leg lowers it. No legs summed yet (the first
        // arrival) ⇒ clean.
        var runningRank = RankConfident;

        // Becomes true the moment an Unknown leg (rank 0) is summed: this arrival and
        // every downstream arrival + finish/return + total are unknown thereafter.
        var unknownFromHere = false;

        // arrival(1) = tripStart (offset 0), clean and known by definition.
        arrivals.Add(MakeArrival(stops[0].PoiId, cumulativeSeconds, tripStart, runningRank, isUnknown: false));

        // Departure from stop k = arrival(k) + Dwell(k); arrival(k+1) = departure(k) + Travel(k→k+1).
        for (var k = 0; k < stops.Count - 1; k++)
        {
            // Dwell at stop k (the Start's dwell at k==0 is thereby counted exactly once).
            cumulativeSeconds += DwellSeconds(stops[k].DwellMinutes);

            // Fold in the leg k→k+1.
            var leg = legs[k];
            runningRank = Math.Min(runningRank, RankOf(leg));
            if (RankOf(leg) == RankUnknown || leg.DurationSeconds is not { } travel)
            {
                unknownFromHere = true;
            }
            else
            {
                cumulativeSeconds += travel;
            }

            arrivals.Add(unknownFromHere
                ? UnknownArrival(stops[k + 1].PoiId)
                : MakeArrival(stops[k + 1].PoiId, cumulativeSeconds, tripStart, runningRank, isUnknown: false));
        }

        // The finish/return entry. A Roundtrip adds the closing leg (the last leg in the
        // list) from the last stop back to Start, plus the LAST stop's dwell (you dwell
        // at the final stop before departing for home). An open path ends at the Finish
        // (the last stop's arrival) — no extra leg, no return entry.
        ItineraryArrival? finishOrReturn;
        if (isRoundtrip)
        {
            // Dwell at the final routed stop, then the closing leg back to Start.
            cumulativeSeconds += DwellSeconds(stops[^1].DwellMinutes);
            var closing = legs[^1];
            runningRank = Math.Min(runningRank, RankOf(closing));
            if (RankOf(closing) == RankUnknown || closing.DurationSeconds is not { } travel)
            {
                unknownFromHere = true;
            }
            else
            {
                cumulativeSeconds += travel;
            }

            finishOrReturn = unknownFromHere
                ? UnknownArrival(stops[0].PoiId)
                : MakeArrival(stops[0].PoiId, cumulativeSeconds, tripStart, runningRank, isUnknown: false);
        }
        else
        {
            // Open path: the terminal entry IS the Finish stop's arrival (already in the
            // routed sequence). Mirror it as the finish/return readout for the UI's
            // end-of-list slot.
            finishOrReturn = arrivals[^1];
        }

        // Total = the terminal cumulative offset (travel + every routed dwell) PLUS every
        // unplaceable stop's dwell. Unknown total iff the terminal entry is unknown.
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

        // Budget overrun is asserted ONLY when a budget is set AND the total is known AND
        // exceeds it. An uncertain total never trips a false overrun.
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

    // The fidelity rank of a leg: Unknown (Placeholder OR null duration) = 0,
    // Estimated = 1, Manual/Measured = 2.
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
            // Placeholder, null, or any unrecognized value ⇒ Unknown (never guess).
            _ => RankUnknown,
        };
    }

    // A surviving Estimated rank qualifies the arrival as Estimated; all-confident ⇒ no
    // qualifier (clean). Rank 0 never reaches here (those arrivals are IsUnknown).
    private static string? QualifierFor(int rank) =>
        rank == RankEstimated ? Fidelity.Estimated : null;

    // Convert dwell minutes → canonical seconds at the computation edge (AR-11). Null /
    // negative dwell contributes zero (an unset dwell is just "no stay").
    private static int DwellSeconds(int? minutes) =>
        minutes is { } m && m > 0 ? m * 60 : 0;
}
