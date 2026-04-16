namespace LucidCartographer.Services.Import;

/// <summary>
/// Metadata for a saved Google Maps list discovered on the user's account.
/// </summary>
public sealed record SavedListInfo(string Name, string Url, int? PlaceCount);
