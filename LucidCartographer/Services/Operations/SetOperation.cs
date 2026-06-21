namespace LucidCartographer.Services.Operations;

/// <summary>
/// Set operation types for POI collections.
/// </summary>
public enum SetOperation
{
    /// <summary>A - B: POIs in A that are not in B.</summary>
    Subtract,
    /// <summary>A intersect B: POIs present in both A and B.</summary>
    Intersect,
    /// <summary>A union B: All unique POIs from both collections.</summary>
    Union,
    /// <summary>Remove duplicates within A.</summary>
    Dedup
}
