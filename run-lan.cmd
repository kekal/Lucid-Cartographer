@echo off
REM Debug: run the app bound to all interfaces so it is reachable from the LAN.
REM Development env => Auth:BypassLocalAddresses=true, so RFC1918 clients skip login.
set ASPNETCORE_ENVIRONMENT=Development
dotnet run --project LucidCartographer --urls http://0.0.0.0:5087
