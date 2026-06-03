#!/bin/sh
# Starts the virtual display + VNC stack that lets the user drive the server-side
# headful Chromium (for the Google sign-in) from their browser via noVNC, then
# launches the app. All VNC services bind to localhost only — they are reachable
# solely through the app's authenticated same-origin proxy (/google-session/novnc).
set -e

export DISPLAY="${DISPLAY:-:99}"
DISPLAY_NUM="${DISPLAY#:}"

# Virtual framebuffer for the headful browser.
Xvfb "$DISPLAY" -screen 0 1280x900x24 -nolisten tcp &

# Wait for Xvfb to create its socket before starting x11vnc / the app.
i=0
while [ ! -e "/tmp/.X11-unix/X${DISPLAY_NUM}" ]; do
    i=$((i + 1))
    if [ "$i" -gt 100 ]; then
        echo "Xvfb did not start within 10s" >&2
        break
    fi
    sleep 0.1
done

# VNC server on the virtual display (localhost only, no password — the app proxy
# enforces auth). -bg forks to the background once it is listening.
x11vnc -display "$DISPLAY" -localhost -forever -shared -nopw -rfbport 5900 -bg -quiet

# noVNC web client + websocket bridge (localhost only).
websockify --web=/usr/share/novnc 127.0.0.1:6080 127.0.0.1:5900 &

exec dotnet LucidCartographer.dll
