namespace LucidCartographer.Services.Trip;

/// <summary>
/// Signals that the Valhalla provider could not produce a usable Measured route
/// for a leg (bad/unparseable response, unreachable host, timeout, no-route, or
/// missing geometry). Thrown to trigger degradation to straight-line Estimated;
/// there is no second fallback. Distinct from <see cref="OperationCanceledException"/>,
/// which is re-thrown for genuine caller-token cancellation.
/// </summary>
public sealed class ValhallaRouteUnavailableException : Exception
{
    /// <summary>Creates the exception with an explanatory message.</summary>
    public ValhallaRouteUnavailableException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with an explanatory message and the underlying cause.</summary>
    public ValhallaRouteUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
