using LucidCartographer.Data;
using LucidCartographer.Services;
using LucidCartographer.Services.Trip;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidCartographer.Tests;

public static class TestDbHelper
{
    public static IDbContextFactory<AppDbContext> CreateFactory(string? dbName = null)
    {
        dbName ??= Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TestDbContextFactory(options);
    }

    /// <summary>
    /// A real <see cref="RouteSegmentInvalidationService"/> over the given factory
    /// and a fresh write lock — for tests exercising the coordinate-change /
    /// recompute invalidation hooks (Story 2.4, TRIP-INVALIDATE-01).
    /// </summary>
    public static IRouteSegmentInvalidationService CreateInvalidationService(
        IDbContextFactory<AppDbContext> factory, SqliteWriteLock? writeLock = null) =>
        new RouteSegmentInvalidationService(
            factory, writeLock ?? new SqliteWriteLock(),
            NullLogger<RouteSegmentInvalidationService>.Instance);
}

public class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext()
    {
        return new AppDbContext(options);
    }
}