namespace LucidCartographer.Services.Import
{
    /// <summary>
    /// Abstraction over the background-job library (Coravel today). Keeps
    /// call sites library-agnostic and lets tests swap in a synchronous
    /// in-process implementation if ever needed.
    /// </summary>
    public interface IImportJobQueue
    {
        /// <summary>
        /// Enqueue an import. Returns immediately once the job is accepted
        /// by the queue; the actual work runs on Coravel's background thread
        /// inside its own DI scope, independent of the caller's request
        /// lifecycle.
        /// </summary>
        void Enqueue(ImportJobPayload payload);
    }
}
