namespace LucidCartographer.Services.Trip;

/// <summary>
/// Signals that the OSRM provider could not produce a usable Measured route
/// for a leg (bad response, unreachable host, timeout, or no configured profile URL).
/// Thrown to trigger degradation to straight-line Estimated; there is no second
/// fallback. Distinct from <see cref="OperationCanceledException"/>, which is re-thrown.
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
