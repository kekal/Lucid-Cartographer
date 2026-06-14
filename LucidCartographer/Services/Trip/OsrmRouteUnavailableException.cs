namespace LucidCartographer.Services.Trip;

/// <summary>
/// TRIP-OSRM-01 (Story 4.1): signals that the OSRM provider could not produce a
/// usable Measured route for a leg — OSRM returned a non-<c>"Ok"</c> code
/// (e.g. <c>"NoRoute"</c>/<c>"NoSegment"</c>), an empty <c>routes</c> array, a
/// non-success HTTP status, an unreachable host, or a request timeout; or the
/// requested mode has no configured profile URL.
///
/// The provider THROWS this (AC3) so the existing background-service catch
/// (TRIP-DEGRADE-01) degrades the leg to a straight-line Estimated value —
/// there is no second fallback inside the provider. This is distinct from a real
/// <see cref="OperationCanceledException"/>, which the provider re-throws.
/// </summary>
public sealed class OsrmRouteUnavailableException : Exception
{
    public OsrmRouteUnavailableException(string message) : base(message)
    {
    }

    public OsrmRouteUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
