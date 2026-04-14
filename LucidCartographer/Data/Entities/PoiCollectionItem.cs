namespace LucidCartographer.Data.Entities
{
    public class PoiCollectionItem
    {
        public int PoiId { get; set; }
        public Poi Poi { get; set; } = null!;
        public int PoiCollectionId { get; set; }
        public PoiCollection PoiCollection { get; set; } = null!;
    }
}
