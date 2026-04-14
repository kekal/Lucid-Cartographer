namespace LucidCartographer.Data.Entities
{
    public class PoiTag
    {
        public int PoiId { get; set; }
        public Poi Poi { get; set; } = null!;

        public int TagId { get; set; }
        public Tag Tag { get; set; } = null!;
    }
}
