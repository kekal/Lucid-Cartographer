# Component Inventory

_Blazor UI layer under `Components/`. Pattern: pages inject a ViewModel, subscribe to `StateChanged`, branch desktop/mobile on `Viewport.IsMobile`._

## Pages (`Components/Pages/`)

| Page | Route | ViewModel | Purpose |
|------|-------|-----------|---------|
| MapPage.razor | `/` | MapPageViewModel | Main map + list; collection visibility, POI detail, search. Desktop: sidebar + LeafletMap + PoiTable + PoiDetailPane. Mobile: search + map + bottom panel + tab bar. |
| DataSourcesPage.razor | `/datasources` | DataSourcesPageViewModel | Import (GPX/KML/GeoJSON/CSV/Takeout/shared lists), collection management, export to My Maps & Google saved lists, enrichment/rename dialogs. Mobile → MobileSourcesScreen. |
| OperationsPage.razor | `/operations` | OperationsPageViewModel | Set operations + spatial tolerance slider, virtualized result table, commit & export, whole-DB dedup. Mobile → MobileOperationsScreen. |
| GoogleSessionPage.razor | `/google-session` | GoogleSessionPageViewModel | Google sign-in status for the shared browser, sign-in trigger, profile reset, embedded noVNC remote view. |
| MorePage.razor | `/more` | — | Mobile-only tab stub; redirects desktop to map. |
| Login.razor | `/login` | — | Auth form (LoginLayout, no chrome). |
| Error.razor | — | — | Static error fallback. |

All interactive pages use `@rendermode InteractiveServer`.

## ViewModels (`Components/Pages/*ViewModel.cs`, plus `Components/Shared/Trip/TripViewModel.cs`)

Each is `sealed`, uses primary-constructor DI, exposes `event Action? StateChanged` + `Notify()`, registered `Transient`.

- **MapPageViewModel** — collections/visibility state, filtered/visible POIs, selected POI, map bounds, sidebar/table sizing, label toggle, search handling, single + batch POI commands (membership, copy, delete, copy-to-new). Deps: `IPoiService`, `NavigationManager`, `EnrichmentProgressService`, `EnrichmentTrigger`, `ILogger`. Uses `RendererDispatch` + `[JSInvokable]` splitter/sidebar resize callbacks; attaches the `LeafletMap` ref after init.
- **DataSourcesPageViewModel** — upload/import state, scrape + "Fetch My Lists", saved-list selection, add-POI, color/rename dialogs, export state, enrichment maintenance. Deps: import/export queues + status services, `IPoiService`, `IGoogleMapsListScraper`, exporters, `IJSRuntime`, `EnrichmentTrigger`. Subscribes to Rx import/export status streams.
- **OperationsPageViewModel** — source A/B selection, tolerance (debounced 500ms re-run), active op, result/discard state, commit dialog, whole-DB dedup. Deps: `IPoiService`, `ISetOperationService`, `IPoiDeduplicationService`, exporters, `IJSRuntime`.
- **GoogleSessionPageViewModel** — remote-view URL, signed-in status, busy flags. Deps: `IBrowserSession`, `IOptions<BrowserOptions>`, `ILogger`.
- **TripViewModel** (`Components/Shared/Trip/`) — Trip View on/off, Stop Order projection, **per-leg-mode** leg projection (`OrderedLegs` with `TripLeg.Mode`), reconciled travel-time/timeline state, Start/Finish, multi-day schedule edits, OSM routing attribution. Wave-2 surface: `CanonicalStopOrder`/`ApplyCanonicalOrder` (plain list shares the canonical order), `RecommendsOsrm` (Mock-default OSRM note), `SetLegModeAsync` / `SetManualLegTimeAsync` / `ClearManualLegTimeAsync` (per-leg mode + manual edit/reset, `MaxManualLegMinutes`), `SetTripStartTimeAsync` / `SetTimeBudgetMinutesAsync`, `IsRoundtrip`. Delegates all order/mode mutation to `ITripOrderingService`, subscribes to `TravelTimeProgressService`, signals `TravelTimeTrigger`. Deps: `ITripOrderingService`, `IDbContextFactory<AppDbContext>`, `SqliteWriteLock`, `TravelTimeTrigger`, `TravelTimeProgressService`, `IRouteSegmentInvalidationService`, optional `ITravelTimeProvider`.

## Shared Components (`Components/Shared/`)

**Framework/map:** `LeafletMap` (Leaflet.js wrapper; marker/bounds callbacks; show/hide/focus/fit/highlight/labels JS methods), `ViewportObserver` (circuit-scoped resize listener feeding `ViewportService`; re-applies theme on nav), `MobileTabBar` (static bottom nav).

**Detail & modals:** `PoiDetailPane` (desktop right pane; inline rename, "use Google name", enrich/manual-enrich/delete), `MobilePoiDetail` (full-screen modal with hero), `MobileModalScreen` (reusable slide-in modal, Escape/back to exit), `EnrichFallbackDialog` (manual Google Maps URL entry).

**Data & management:** `CollectionSidebar` (desktop collection list with visibility toggles), `PoiTable` (desktop virtualized table with select-all + batch move/copy/delete), `FileUploadPanel` (drag-drop import + collection name/color), `ScraperPanel` (shared-list URL, fetch lists, saved-list picker, profile reset), `EnrichmentStatus` (Rx-driven counter island in the static header).

**Mobile screens:** `MobileSourcesScreen`, `MobileOperationsScreen`, `MobileMoreScreen`.

**Trip Planning (`Components/Shared/Trip/`):** `TripToggle` / `MobileTripToggle` (Trip View switch in the filtered-results region, `aria-pressed`, ≥2-placeable gate), `TripStopList` (Wave-2 desktop **wide CSS-grid trip table** that replaces `PoiTable` in the bottom region when Trip View is on, with inline schedule controls + finish/return footer) / `MobileTripPanel` (Wave-1 mobile stop rows), `LegConnector` (Wave-2 inter-row leg strip: time/distance/`FidelityBadge` + click-to-edit manual time + reset, hosting the mode pill), `LegModePill` (Wave-2 per-leg Walk/Drive/Cycle/Any-Air menu), `StopOrderBadge`, `FidelityBadge` (self-explaining Measured/Estimated/Manual pill; "—" for unmeasured), and the **legacy** `TravelModeSelector` (trip-wide segmented control — now **inert, mobile-only**, replaced on desktop by `LegModePill`). State lives in `TripViewModel`; read-model types in `TripProjections.cs` (`TripLeg`, `TripStop`, `TripStopRow`). The Wave-2 per-leg-mode/connector/schedule **controls** are desktop-only; the shared logic reaches mobile by nature. See [trip-planning.md](./trip-planning.md).

## Layout (`Components/Layout/`)

- **MainLayout** — primary shell: desktop top nav (Map / Data Sources / Operations) + search + enrichment status + Google session/logout; `ErrorBoundary` around `@Body`; mobile tab bar via CSS.
- **LoginLayout** — minimal, chrome-free.

## Lifecycle Bridge (canonical)

```csharp
protected override async Task OnInitializedAsync()
{
    Vm.StateChanged += OnVmChanged;
    Viewport.Changed += OnViewportChanged;
    await Vm.InitializeAsync();
}
private void OnVmChanged() => InvokeAsync(StateHasChanged);
private void OnViewportChanged() => InvokeAsync(StateHasChanged);
public async ValueTask DisposeAsync()
{
    Vm.StateChanged -= OnVmChanged;
    Viewport.Changed -= OnViewportChanged;
    await Vm.DisposeAsync();
}
```

## JS Interop (`wwwroot/js/`)

`leafletInterop.js` (map layers, markers, bounds, mobile mode, splitter/drag, geolocation, `downloadFile`), `viewport.js` (`LucidViewport.register/unregister`), `history.js` (mobile back-button routing), `theme.js` (`LucidTheme.apply`), `reconnect.js` (circuit reconnection UI). UI strings come from `Services/UiStrings.cs` (no hardcoded text).
