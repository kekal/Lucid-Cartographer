using System.ComponentModel.DataAnnotations;

namespace LucidCartographer.Data.Entities
{
    public class PoiCollection
    {
        public int Id { get; set; }

        [MaxLength(500)]
        public string Name { get; set; } = string.Empty;

        [MaxLength(2000)]
        public string? Description { get; set; }

        [MaxLength(9)]
        public string Color { get; set; } = "#005bbf";

        [MaxLength(100)]
        public string? IconName { get; set; }

        public bool IsVisible { get; set; } = true;

        public DateTime CreatedDate { get; set; }

        [MaxLength(50)]
        public string? SourceType { get; set; } // Use CollectionSourceType constants

        [MaxLength(500)]
        public string? SourceFileName { get; set; }

        /// <summary>
        /// Denormalized count — computed from DB in GetCollectionsAsync for accuracy.
        /// May also be set during import for convenience.
        /// </summary>
        public int PoiCount { get; set; }

        [ConcurrencyCheck]
        public int Version { get; set; }

        public List<PoiCollectionItem> CollectionItems { get; set; } = new();
    }
}
