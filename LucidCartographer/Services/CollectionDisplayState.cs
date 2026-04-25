using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services
{
    /// <summary>
    /// Wraps a PoiCollection entity with UI-only display state.
    /// Visibility toggling modifies this wrapper, not the entity directly,
    /// avoiding shared mutable state between the UI and EF Core change tracking.
    /// </summary>
    public class CollectionDisplayState
    {
        public PoiCollection Collection { get; }

        /// <summary>
        /// UI-only visibility flag, decoupled from the entity's persisted IsVisible.
        /// </summary>
        public bool IsVisible { get; set; }

        public CollectionDisplayState(PoiCollection collection)
        {
            Collection = collection;
            IsVisible = collection.IsVisible;
        }

        // Convenience pass-through properties
        public int Id => Collection.Id;
        public string Name => Collection.Name;
        public string Color => Collection.Color;
        public int PoiCount => Collection.PoiCount;
    }
}
