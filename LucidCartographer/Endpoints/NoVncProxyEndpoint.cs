using System.Net.WebSockets;
using LucidCartographer.Services.Browser;
using Microsoft.Extensions.Options;

namespace LucidCartographer.Endpoints;

/// <summary>
/// Same-origin reverse proxy for the noVNC web client + its websocket, forwarding
/// to the loopback <c>websockify</c> process that x11vnc feeds (Docker/Linux only).
/// This keeps the remote view behind the app's existing cookie auth — websockify
/// is bound to localhost and never exposed directly. Only mapped when
/// <see cref="RemoteViewOptions.Enabled"/> is true (off in local dev).
/// </summary>
public static class NoVncProxyEndpoint
{
    private const string RoutePrefix = "/google-session/novnc";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public static void MapNoVncProxy(this WebApplication app)
    {
        var remote = app.Services.GetRequiredService<IOptions<BrowserOptions>>().Value.RemoteView;
        if (!remote.Enabled)
        {
            return;
        }

        var backend = $"{remote.WebsockifyHost}:{remote.WebsockifyPort}";

        // {**path} catch-all: serves vnc_lite.html, the noVNC asset tree, and the
        // "websockify" websocket. Not in the route-guard exempt list, so the cookie
        // auth redirect already gates it; we re-check defensively below.
        app.Map($"{RoutePrefix}/{{**path}}", async (HttpContext ctx, string? path) =>
        {
            if (!(ctx.User.Identity?.IsAuthenticated ?? false))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            path ??= string.Empty;

            if (ctx.WebSockets.IsWebSocketRequest)
            {
                await ProxyWebSocketAsync(ctx, backend, path);
            }
            else
            {
                await ProxyHttpAsync(ctx, backend, path);
            }
        });
    }

    private static async Task ProxyHttpAsync(HttpContext ctx, string backend, string path)
    {
        var target = $"http://{backend}/{path}{ctx.Request.QueryString}";
        try
        {
            using var upstream = await Http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
            ctx.Response.StatusCode = (int)upstream.StatusCode;
            if (upstream.Content.Headers.ContentType is { } contentType)
            {
                ctx.Response.ContentType = contentType.ToString();
            }
            await upstream.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        }
        catch (Exception) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // Client navigated away — nothing to do.
        }
        catch (Exception)
        {
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
            }
        }
    }

    private static async Task ProxyWebSocketAsync(HttpContext ctx, string backend, string path)
    {
        // Preserve the noVNC subprotocol negotiation ("binary" / "base64").
        var requested = ctx.WebSockets.WebSocketRequestedProtocols;

        using var client = new ClientWebSocket();
        foreach (var proto in requested)
        {
            client.Options.AddSubProtocol(proto);
        }

        var target = new Uri($"ws://{backend}/{path}{ctx.Request.QueryString}");
        try
        {
            await client.ConnectAsync(target, ctx.RequestAborted);
        }
        catch (Exception)
        {
            ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using var server = await ctx.WebSockets.AcceptWebSocketAsync(client.SubProtocol);

        var clientToServer = PumpAsync(client, server, ctx.RequestAborted);
        var serverToClient = PumpAsync(server, client, ctx.RequestAborted);
        await Task.WhenAny(clientToServer, serverToClient);

        await CloseQuietlyAsync(server);
        await CloseQuietlyAsync(client);
    }

    private static async Task PumpAsync(WebSocket from, WebSocket to, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (from.State == WebSocketState.Open && to.State == WebSocketState.Open)
            {
                var result = await from.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await to.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                    return;
                }
                await to.SendAsync(
                    new ArraySegment<byte>(buffer, 0, result.Count),
                    result.MessageType, result.EndOfMessage, ct);
            }
        }
        catch (Exception)
        {
            // Either side closed/aborted — let the caller tear down both sockets.
        }
    }

    private static async Task CloseQuietlyAsync(WebSocket socket)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
        }
        catch (Exception) { /* best effort */ }
    }
}
