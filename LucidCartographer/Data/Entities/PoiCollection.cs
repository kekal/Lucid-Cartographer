using System.ComponentModel.DataAnnotations.Schema;

namespace LucidCartographer.Data.Entities
{
    public class PoiCollection
    {
        public int Id { get; set; }

        public required string Name { get; set; }

        public string? Description { get; set; }

        public required string Color { get; set; } = "#005bbf";

        public string? IconName { get; set; }

        public bool IsVisible { get; set; } = true;

        public DateTime CreatedDate { get; set; }

        public string? SourceType { get; set; } // Use CollectionSourceType constants

        public string? SourceFileName { get; set; }

        /// <summary>
        /// Not persisted — computed from DB at read time in GetCollectionsAsync.
        /// </summary>
        [NotMapped]
        public int PoiCount { get; set; }

        [System.ComponentModel.DataAnnotations.ConcurrencyCheck]
        public int Version { get; set; }

        public List<PoiCollectionItem> CollectionItems { get; set; } = new();
    }
}
