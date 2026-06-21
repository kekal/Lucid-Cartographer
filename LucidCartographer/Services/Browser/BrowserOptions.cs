namespace LucidCartographer.Services.Browser;

/// <summary>
/// Configuration for the shared headful Chromium session (Saved-List export,
/// authenticated scrape, Google sign-in). Bound from the <c>Browser</c> config section.
/// </summary>
public sealed class BrowserOptions
{
    public const string SectionName = "Browser";

    /// <summary>
    /// Persistent Chrome profile directory (env var → config → default dev path).
    /// In Docker, points to the persisted <c>/data</c> volume for sign-in persistence.
    /// </summary>
    public string? ProfilePath { get; set; }

    /// <summary>
    /// Launch Chromium headless. Defaults to false — session must be headful
    /// for user sign-in via noVNC and Xvfb rendering.
    /// </summary>
    public bool Headless { get; set; }

    /// <summary>noVNC remote-view settings (Docker/Linux only).</summary>
    public RemoteViewOptions RemoteView { get; set; } = new();
}

/// <summary>
/// Settings for the embedded noVNC remote view of the server-side headful
/// Chromium session (allows user to sign in via their browser).
/// </summary>
public sealed class RemoteViewOptions
{
    /// <summary>
    /// Proxy noVNC at <c>/google-session/novnc</c> for embedded remote view.
    /// Enabled in Docker; disabled in local dev (real window appears).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Loopback host where websockify serves noVNC. Never exposed directly.</summary>
    public string WebsockifyHost { get; set; } = "127.0.0.1";

    /// <summary>Loopback port where websockify serves noVNC + the websocket.</summary>
    public int WebsockifyPort { get; set; } = 6080;
}
