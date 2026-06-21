namespace LucidCartographer.Services.Export;

/// <summary>
/// Abstraction over the background-job library, allowing library-agnostic call sites and testable synchronous swaps.
/// </summary>
public interface IExportJobQueue
{
    /// <summary>
    /// Enqueue an export; returns immediately while the heavy work runs on a background thread in its own DI scope, independent of the caller's Blazor circuit.
    /// </summary>
    void Enqueue(ExportJobPayload payload);
}
