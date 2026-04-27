using FluentAssertions;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Auth;
using Microsoft.EntityFrameworkCore;

namespace LucidCartographer.Tests;

/// <summary>
/// SessionStore — was 0% covered. Uses the in-memory provider via
/// TestDbHelper.CreateFactory so the create / validate / revoke /
/// expiry paths are exercised end-to-end.
/// </summary>
public class SessionStoreTests
{
    [Fact]
    public async Task CreateAsync_PersistsTokenHash_AndReturnsRawToken()
    {
        var factory = TestDbHelper.CreateFactory();
        var store = new SessionStore(factory);

        var token = await store.CreateAsync(CancellationToken.None);

        token.Should().NotBeNullOrWhiteSpace();
        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.Sessions.ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].TokenHash.Should().NotBe(token, "the DB stores a hash, never the raw token");
        rows[0].RevokedAt.Should().BeNull();
        rows[0].ExpiresAt.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task IsActiveAsync_ReturnsTrue_ForFreshlyCreatedToken()
    {
        var factory = TestDbHelper.CreateFactory();
        var store = new SessionStore(factory);
        var token = await store.CreateAsync(CancellationToken.None);

        var active = await store.IsActiveAsync(token, CancellationToken.None);

        active.Should().BeTrue();
    }

    [Fact]
    public async Task IsActiveAsync_ReturnsFalse_ForUnknownToken()
    {
        var factory = TestDbHelper.CreateFactory();
        var store = new SessionStore(factory);

        var active = await store.IsActiveAsync("unknown-random-token", CancellationToken.None);

        active.Should().BeFalse();
    }

    [Fact]
    public async Task IsActiveAsync_ReturnsFalse_ForEmptyToken()
    {
        var factory = TestDbHelper.CreateFactory();
        var store = new SessionStore(factory);

        (await store.IsActiveAsync("", CancellationToken.None)).Should().BeFalse();
        (await store.IsActiveAsync("   ", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task RevokeAsync_FlipsActiveToFalse()
    {
        var factory = TestDbHelper.CreateFactory();
        var store = new SessionStore(factory);
        var token = await store.CreateAsync(CancellationToken.None);

        await store.RevokeAsync(token, CancellationToken.None);

        (await store.IsActiveAsync(token, CancellationToken.None)).Should().BeFalse();
        await using var db = await factory.CreateDbContextAsync();
        var session = await db.Sessions.SingleAsync();
        session.RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RevokeAsync_IsIdempotent()
    {
        var factory = TestDbHelper.CreateFactory();
        var store = new SessionStore(factory);
        var token = await store.CreateAsync(CancellationToken.None);

        await store.RevokeAsync(token, CancellationToken.None);
        // Second call should not throw or change RevokedAt back to null.
        await store.RevokeAsync(token, CancellationToken.None);

        await using var db = await factory.CreateDbContextAsync();
        var session = await db.Sessions.SingleAsync();
        session.RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RevokeAsync_WithEmptyToken_IsNoOp()
    {
        var factory = TestDbHelper.CreateFactory();
        var store = new SessionStore(factory);

        // Must not throw, must not flip any sessions.
        await store.RevokeAsync("", CancellationToken.None);
        await store.RevokeAsync("   ", CancellationToken.None);
    }

    [Fact]
    public async Task IsActiveAsync_ReturnsFalse_ForExpiredSession()
    {
        var factory = TestDbHelper.CreateFactory();
        var store = new SessionStore(factory);
        var token = await store.CreateAsync(CancellationToken.None);

        // Force-expire the row directly so we don't have to wait 30 days.
        await using (var db = await factory.CreateDbContextAsync())
        {
            var row = await db.Sessions.SingleAsync();
            row.ExpiresAt = DateTime.UtcNow - TimeSpan.FromHours(1);
            await db.SaveChangesAsync();
        }

        (await store.IsActiveAsync(token, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task TwoCalls_GenerateDistinctTokens()
    {
        var factory = TestDbHelper.CreateFactory();
        var store = new SessionStore(factory);

        var a = await store.CreateAsync(CancellationToken.None);
        var b = await store.CreateAsync(CancellationToken.None);

        a.Should().NotBe(b);
    }
}
