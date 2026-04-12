namespace LucidCartographer.Services.Import
{
    /// <summary>
    /// Result of an import operation containing counts and collection metadata.
    /// </summary>
    public record ImportResult
    {
        public int AddedCount { get; init; }
        public int SkippedCount { get; init; }
        public int TotalParsed { get; init; }
        public int CollectionId { get; init; }
        public string CollectionName { get; init; } = string.Empty;
    }
}
