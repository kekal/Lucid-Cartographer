using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services.Operations;

/// <summary>
/// Result of a set operation, containing the resulting POIs and metadata.
/// OPS-R16: Pois exposed as IReadOnlyList to prevent callers from mutating the result.
/// OPS-R18: Extracted into its own file from SetOperationService.cs.
/// </summary>
public class OperationResult
{
    /// <summary>The resulting POIs from the operation.</summary>
    public IReadOnlyList<Poi> Pois { get; init; } = [];

    /// <summary>Duplicate groups found (only populated for Dedup operations).</summary>
    public IReadOnlyList<List<Poi>>? DuplicateGroups { get; init; }

    /// <summary>Human-readable description of the operation result.</summary>
    public string Description { get; init; } = string.Empty;
}
