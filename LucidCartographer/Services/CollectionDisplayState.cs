using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services;

/// <summary>
/// UI state wrapper for PoiCollection; visibility changes do not affect persisted entity or EF Core tracking.
/// </summary>
public class CollectionDisplayState(PoiCollection collection)
{
    public PoiCollection Collection { get; } = collection;

    /// <summary>
    /// UI-only visibility flag, decoupled from the entity's persisted IsVisible.
    /// </summary>
    public bool IsVisible { get; set; } = collection.IsVisible;

    public int Id => Collection.Id;
    public string Name => Collection.Name;
    public string Color => Collection.Color;
    public int PoiCount => Collection.PoiCount;
}
