namespace LucidCartographer.Services.Export;

/// <summary>
/// Abstraction over the background-job library (Coravel today) for Google
/// Saved-List exports. Keeps call sites library-agnostic and lets tests swap in
/// a synchronous implementation.
/// </summary>
public interface IExportJobQueue
{
    /// <summary>
    /// Enqueue an export. Returns immediately once accepted; the headful-browser
    /// work runs on Coravel's background thread inside its own DI scope,
    /// independent of the caller's Blazor circuit (the user may navigate away).
    /// </summary>
    void Enqueue(ExportJobPayload payload);
}
