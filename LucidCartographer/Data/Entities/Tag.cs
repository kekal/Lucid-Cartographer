namespace LucidCartographer.Data.Entities;

public class Tag
{
    public int Id { get; set; }
    public required string Name { get; set; }

    public List<PoiTag> PoiTags { get; set; } = [];
}
