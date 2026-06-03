using LucidCartographer.Services.Browser;
using Microsoft.Extensions.Options;

namespace LucidCartographer.Components.Pages;

/// <summary>
/// View-side orchestration for the Google session page: surfaces the shared
/// browser's sign-in state, opens the Google sign-in in that same shared browser
/// (which the user drives via the embedded noVNC view on the server), and can
/// reset the persistent profile to switch accounts.
/// </summary>
public sealed class GoogleSessionPageViewModel(
    IBrowserSession session,
    IOptions<BrowserOptions> options,
    ILogger<GoogleSessionPageViewModel> logger)
    : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public event Action? StateChanged;

    private void Notify() => StateChanged?.Invoke();

    /// <summary>True when the embedded noVNC remote view is available (Docker/Linux).</summary>
    public bool RemoteViewEnabled => options.Value.RemoteView.Enabled;

    /// <summary>
    /// Same-origin noVNC client URL (proxied + behind cookie auth). resize=scale
    /// scales the fixed remote framebuffer to fit the iframe (x11vnc can't resize
    /// the Xvfb display, so resize=remote would overflow with scrollbars).
    /// </summary>
    public string NoVncUrl =>
        "/google-session/novnc/vnc_lite.html" +
        "?path=google-session/novnc/websockify&autoconnect=1&resize=scale&reconnect=1";

    /// <summary>
    /// Whether to render the embedded remote view. Hidden until the user clicks
    /// "Open Google sign-in" so the VNC connection isn't opened until needed.
    /// </summary>
    public bool ShowRemoteView { get; private set; }

    public bool IsBusy { get; private set; }

    /// <summary>null = unknown / could not determine; true/false otherwise.</summary>
    public bool? SignedIn { get; private set; }

    public string? StatusMessage { get; private set; }

    public async Task RefreshStatusAsync()
    {
        IsBusy = true;
        StatusMessage = "Checking sign-in status…";
        Notify();
        try
        {
            var status = await session.GetStatusAsync(_cts.Token);
            SignedIn = status.Busy ? null : status.SignedIn;
            StatusMessage = status.Detail;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to refresh Google session status");
            SignedIn = null;
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            Notify();
        }
    }

    public async Task OpenSignInAsync()
    {
        IsBusy = true;
        ShowRemoteView = true; // reveal the live view so the user can complete the login
        StatusMessage = "Opening the Google sign-in page…";
        Notify();
        try
        {
            var status = await session.NavigateToSignInAsync(_cts.Token);
            StatusMessage = status.Detail;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to open Google sign-in");
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            Notify();
        }
    }

    public async Task ResetProfileAsync()
    {
        IsBusy = true;
        StatusMessage = "Resetting the browser profile…";
        Notify();
        try
        {
            await session.ResetProfileAsync(_cts.Token);
            SignedIn = false;
            StatusMessage = "Profile reset. Open Google sign-in to log in again.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to reset browser profile");
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            Notify();
        }
    }

    public ValueTask DisposeAsync()
    {
        // The Razor component calls this AND the DI scope disposes the transient
        // VM, so DisposeAsync runs twice — guard the CTS against double-dispose.
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }
        _disposed = true;
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* already disposed */ }
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
