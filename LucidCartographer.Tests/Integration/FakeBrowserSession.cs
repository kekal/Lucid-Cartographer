using LucidCartographer.Services.Browser;
using Microsoft.Playwright;

namespace LucidCartographer.Tests.Integration;

/// <summary>
/// Test double for <see cref="IBrowserSession"/>. Integration tests must never
/// launch a real headful Chromium session, so page acquisition throws; the
/// status / sign-in surface returns "not signed in" without touching a browser.
/// </summary>
public sealed class FakeBrowserSession : IBrowserSession
{
    public string ProfilePath { get; } =
        Path.Combine(Path.GetTempPath(), "cartographer_test_profile");

    public bool HasProfile => false;

    public Task<IPage> NewPageAsync(CancellationToken ct = default)
        => throw new NotSupportedException("FakeBrowserSession does not launch a real browser.");

    public Task<IPage> NewMobilePageAsync(CancellationToken ct = default)
        => throw new NotSupportedException("FakeBrowserSession does not launch a real browser.");

    public Task<GoogleSessionStatus> GetStatusAsync(CancellationToken ct = default)
        => Task.FromResult(new GoogleSessionStatus(SignedIn: false, Busy: false, "Not signed in (test)."));

    public Task<GoogleSessionStatus> NavigateToSignInAsync(CancellationToken ct = default)
        => Task.FromResult(new GoogleSessionStatus(SignedIn: false, Busy: false, "Sign-in not available in tests."));

    public Task ResetProfileAsync(CancellationToken ct = default) => Task.CompletedTask;
}
