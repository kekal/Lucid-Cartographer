namespace LucidCartographer.Services.Browser;

/// <summary>
/// Configuration for the single shared headful Chromium session that performs
/// all Google-account-dependent automation (Saved-List export, "Fetch My Lists",
/// authenticated shared-list scrape). Bound from the <c>Browser</c> config section.
/// </summary>
public sealed class BrowserOptions
{
    public const string SectionName = "Browser";

    /// <summary>
    /// Persistent Chrome profile directory. Resolution precedence (applied in
    /// <see cref="BrowserSessionManager"/>): <c>CHROME_PROFILE_PATH</c> env var →
    /// this value → <c>AppContext.BaseDirectory/data/chrome-profile</c> (dev default).
    /// In Docker this points at the persisted <c>/data</c> volume so the Google
    /// sign-in survives container restarts.
    /// </summary>
    public string? ProfilePath { get; set; }

    /// <summary>
    /// Launch Chromium headless. Defaults to <c>false</c> — the session must be
    /// headful so the user can sign in via the noVNC remote view (and so it
    /// renders into the Xvfb display on the server).
    /// </summary>
    public bool Headless { get; set; }

    /// <summary>noVNC remote-view settings (Docker/Linux only).</summary>
    public RemoteViewOptions RemoteView { get; set; } = new();
}

/// <summary>
/// Settings for the embedded noVNC remote view that lets the user drive the
/// server-side headful Chromium (for the Google login) from their own browser.
/// </summary>
public sealed class RemoteViewOptions
{
    /// <summary>
    /// When true, the app proxies noVNC at <c>/google-session/novnc</c> and the
    /// Google session page shows the embedded view. Enabled in Docker (where
    /// Xvfb + x11vnc + websockify run); off in local dev (a real window appears).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Loopback host where websockify serves noVNC. Never exposed directly.</summary>
    public string WebsockifyHost { get; set; } = "127.0.0.1";

    /// <summary>Loopback port where websockify serves noVNC + the websocket.</summary>
    public int WebsockifyPort { get; set; } = 6080;
}
