namespace LucidCartographer.Services.Export;

/// <summary>
/// Payload for a background Google Saved-List export job; place URLs are resolved fresh from DB at run time.
/// </summary>
public sealed class ExportJobPayload
{
    public int CollectionId { get; init; }

    /// <summary>Name of the Google Maps Saved List to create / add to.</summary>
    public string ListName { get; init; } = string.Empty;
}
