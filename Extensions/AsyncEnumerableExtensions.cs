using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Extensions
{
    /// <summary>
    /// Extension methods for IQueryable async enumeration.
    /// </summary>
    internal static class AsyncEnumerableExtensions
    {
        public static async Task<HashSet<T>> ToHashSetAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default)
        {
            var set = new HashSet<T>();
            await foreach (var item in source.AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                set.Add(item);
            }
            return set;
        }
    }
}
