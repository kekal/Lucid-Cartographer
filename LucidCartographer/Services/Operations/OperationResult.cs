using LucidCartographer.Data.Entities;

namespace LucidCartographer.Services.Operations;

/// <summary>Result of a set operation, with POIs exposed as IReadOnlyList to prevent caller mutation.</summary>
public class OperationResult
{
    /// <summary>Resulting POIs from the operation.</summary>
    public IReadOnlyList<Poi> Pois { get; init; } = [];

    /// <summary>Duplicate groups (populated only for Dedup operations).</summary>
    public IReadOnlyList<List<Poi>>? DuplicateGroups { get; init; }

    /// <summary>Human-readable description of the operation result.</summary>
    public string Description { get; init; } = string.Empty;
}
