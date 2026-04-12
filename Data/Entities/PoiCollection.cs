namespace LucidCartographer.Data.Entities
{
    public class PoiCollection
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string Color { get; set; } = "#005bbf";
        public string? IconName { get; set; }
        public bool IsVisible { get; set; } = true;
        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? SourceType { get; set; } // gpx_import, kml_import, manual, operation_result
        public string? SourceFileName { get; set; }
        public int PoiCount { get; set; }

        public List<PoiCollectionItem> CollectionItems { get; set; } = new();
    }
}
