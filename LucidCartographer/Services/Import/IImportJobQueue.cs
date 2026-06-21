namespace LucidCartographer.Services.Import;

/// <summary>Abstraction over the background-job library for library-agnostic call sites and testability.</summary>
public interface IImportJobQueue
{
    /// <summary>Enqueue an import; returns immediately once accepted, work runs on background thread in its own DI scope.</summary>
    void Enqueue(ImportJobPayload payload);
}