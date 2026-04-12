namespace LucidCartographer.Services.Import
{
    public interface IFileImporter
    {
        string FormatName { get; }
        string[] SupportedExtensions { get; }
        Task<List<ImportedPoi>> ParseAsync(Stream fileStream, string fileName);
    }
}
