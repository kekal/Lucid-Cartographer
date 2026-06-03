namespace LucidCartographer.Services.Export;

/// <summary>
/// Payload for a background Google Saved-List export job. Only the collection id
/// and target list name are carried across the queue boundary; the eligible
/// place URLs are resolved fresh from the DB inside <see cref="ExportInvocable"/>
/// (so the export reflects the collection's state at run time, not enqueue time).
/// </summary>
public sealed class ExportJobPayload
{
    public int CollectionId { get; init; }

    /// <summary>Name of the Google Maps Saved List to create / add to.</summary>
    public string ListName { get; init; } = string.Empty;
}
