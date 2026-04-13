# REVIEW 2 -- Blazor UI Components

**Reviewer:** Principal Engineer (Angry)
**Date:** 2026-04-13
**Scope:** All 13 `.razor` files under `Components/`
**Verdict:** The fix cycle patched the arterial bleeding. Congratulations. Now let us discuss the 30+ papercuts that are quietly infecting the patient.

---

## CRITICAL (will bite you in production)

### CRIT-01: CDN assets still unpinned and lacking SRI -- App.razor:12-13

The TODO comment has been sitting here since the last review. Leaflet CSS and JS are loaded from `unpkg.com` with **no `integrity` attribute** and **no `crossorigin` attribute**. A compromised CDN injects arbitrary JS into every user session.

**File:** `Components/App.razor`, lines 12-13
```html
<link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css" />
<script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>
```
**Fix:** Self-host in `wwwroot/lib/leaflet/` or at minimum add `integrity="sha256-..."` and `crossorigin="anonymous"`.

---

### CRIT-02: Google Fonts loaded from external CDN without fallback -- App.razor:8-9

Two external Google Fonts requests (Manrope, Inter, Material Symbols) with zero fallback. If Google Fonts is blocked (corporate firewall, GDPR cookie refusal, network outage), the entire UI renders in Times New Roman and icons vanish. Material Symbols is especially fragile -- every icon in the app is rendered via this external stylesheet.

**File:** `Components/App.razor`, lines 8-9
**Fix:** Self-host font files. At minimum, add proper fallback stacks in CSS (`font-family: 'Manrope', system-ui, sans-serif`). For Material Symbols, provide an SVG sprite fallback or self-host the font.

---

### CRIT-03: `GetGoogleMapsUrl` is copy-pasted across THREE components

The identical static method `GetGoogleMapsUrl(Poi poi)` exists in:
- `OperationsPage.razor` (line 414)
- `PoiTable.razor` (line 102)
- `PoiDetailPane.razor` (line 193)

This is a DRY violation the codebase itself acknowledges with the comment `// HIGH-05/LOW-10: Shared Google Maps URL helper (DRY)` -- and then proceeds to NOT share it. The method is duplicated verbatim. When the Google Maps URL format changes, or you need to add URL encoding for special characters in coordinates with non-dot decimal separators, you will forget one of the three copies.

**Fix:** Extract to a static utility class `PoiUrlHelper.GetGoogleMapsUrl()` and call it from all three locations.

---

### CRIT-04: LeafletMap.DisposeAsync does not dispose the JS map object -- LeafletMap.razor:92-96

```csharp
public async ValueTask DisposeAsync()
{
    MapService.OnMarkerClicked -= HandleMarkerClicked;
    _initTcs.TrySetCanceled();
}
```

The event handler is unsubscribed and the TCS is cancelled. But **the actual Leaflet map JS object is never torn down**. There is no `await MapService.DestroyMapAsync(_mapElementId)` or equivalent. Every time MapPage navigates away and back, you leak a Leaflet map instance on the JS side (layers, tile fetchers, event listeners, the entire L.map object). On a long-lived SignalR circuit with repeated navigation, this is a memory leak that compounds.

**File:** `Components/Shared/LeafletMap.razor`, lines 92-96
**Fix:** Add a `DestroyMapAsync` method to `IMapService` and call it in `DisposeAsync`.

---

### CRIT-05: DataSourcesPage does not implement IDisposable/IAsyncDisposable

`DataSourcesPage.razor` injects `IGoogleMapsListScraper` and can kick off a long-running scrape operation (`ScrapeAsync`). If the user navigates away mid-scrape, the component is disposed but the scrape continues running against a dead circuit. The `InvokeAsync(StateHasChanged)` callback inside the scrape progress handler will throw `ObjectDisposedException` because the component's renderer is gone.

**File:** `Components/Pages/DataSourcesPage.razor`
**Fix:** Implement `IAsyncDisposable`, use a `CancellationTokenSource`, pass the token to `ScrapeAsync`, and cancel on dispose.

---

### CRIT-06: OperationsPage does not implement IDisposable/IAsyncDisposable

Same pattern. `RunOperation` can take significant time for large collections. User navigates away, `SetOperationService.ExecuteAsync` continues, `StateHasChanged` throws. No cancellation support.

**File:** `Components/Pages/OperationsPage.razor`
**Fix:** Implement `IAsyncDisposable` with `CancellationTokenSource`.

---

## HIGH (correctness and reliability issues)

### HIGH-01: MapPage.OnParametersSetAsync runs search on every parameter change without debounce

`OnParametersSetAsync` fires every time any parameter changes. Since the search form uses `method="get"` in the MainLayout header, each keystroke that auto-submits (or each navigation) triggers a full `PoiService.SearchAsync()` call. There is no debouncing, no cancellation of in-flight searches, and no check for duplicate queries.

**File:** `Components/Pages/MapPage.razor`, lines 101-116
**Fix:** Cache the previous search term. Skip if unchanged. Add cancellation token support.

---

### HIGH-02: MainLayout search form uses `method="get"` with `data-enhance="false"` -- full page reload

The search form in MainLayout has `data-enhance="false"` which disables Blazor enhanced navigation. Every search submission does a full-page HTTP GET, tearing down the entire SignalR circuit, destroying all component state (including the Leaflet map), and forcing a complete re-initialization.

**File:** `Components/Layout/MainLayout.razor`, line 30
**Fix:** Replace with Blazor interactive search: use `@onsubmit`, `NavigationManager.NavigateTo`, or an `@oninput` handler with debounce. Remove `data-enhance="false"`.

---

### HIGH-03: `_collections` type mismatch between service return and component field

`IPoiService.GetCollectionsAsync()` returns `Task<List<PoiCollection>>`. MapPage stores it in `IReadOnlyList<PoiCollection>`, which is fine. But `DataSourcesPage` and `OperationsPage` store it in `List<PoiCollection>` -- the **mutable** concrete type. This means child components or event handlers could cast and mutate the list, violating the immutability discipline the codebase claims to follow (per `// HIGH-01: Accept IReadOnlyList`).

**Files:** `DataSourcesPage.razor:271`, `OperationsPage.razor:293`
**Fix:** Use `IReadOnlyList<PoiCollection>` consistently everywhere.

---

### HIGH-04: PoiTable directly invokes EventCallback in @onclick without error handling

```html
@onclick="() => OnPoiSelected.InvokeAsync(poi.Id)"
```

Line 38 of `PoiTable.razor` invokes `OnPoiSelected.InvokeAsync` directly in the markup. Unlike `CollectionSidebar` which wraps callbacks in try/catch methods, `PoiTable` fires and forgets. If the parent handler throws, the exception goes unhandled by the component. This is inconsistent with the pattern established in `CollectionSidebar.razor` lines 60-78.

**File:** `Components/Shared/PoiTable.razor`, line 38
**Fix:** Wrap in a code-behind method with try/catch, matching the pattern in CollectionSidebar.

---

### HIGH-05: `PoiCollection.IsVisible` is mutable entity state toggled via UI -- shared state mutation

`HandleVisibilityToggled` in MapPage calls `PoiService.ToggleVisibilityAsync(collectionId)` which presumably mutates the database entity's `IsVisible` property. Then it re-fetches all collections. But between the toggle and the re-fetch, the old `_collections` list still references the entity objects whose `IsVisible` may have been mutated in place by EF Core change tracking. This is a race condition if anything reads `_collections` during that window.

**File:** `Components/Pages/MapPage.razor`, lines 155-159
**Fix:** Don't mutate entity properties as UI toggle state. Use a separate view-model or dictionary for visibility state.

---

### HIGH-06: LeafletMap.HandleMarkerClicked is async void with Debug.WriteLine error handling

```csharp
private async void HandleMarkerClicked(int poiId)
{
    try { ... }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"...");
    }
}
```

The comment says `async void` is required by the `Action<int>` delegate. But `Debug.WriteLine` is invisible in production. If `OnMarkerSelected.InvokeAsync` throws (e.g., JSDisconnectedException on a dead circuit), nobody notices. The error is silently swallowed.

**File:** `Components/Shared/LeafletMap.razor`, lines 80-89
**Fix:** Use `ILogger` instead of `Debug.WriteLine`. Consider converting `IMapService.OnMarkerClicked` to `Func<int, Task>` to avoid async void entirely.

---

### HIGH-07: OperationsPage ExportResult uses `_commitName` which may be null

```csharp
var bytes = KmlExporter.Export(poisToExport, _commitName ?? "Operation Result");
```

`_commitName` is only populated when `CommitToLayer()` is called. But `ExportResult` can be called independently, before any commit dialog is opened, in which case `_commitName` is `string.Empty` (its initial value), not `null`. The `??` operator won't catch empty string. The export will have an empty title.

**File:** `Components/Pages/OperationsPage.razor`, line 397
**Fix:** Use `string.IsNullOrEmpty(_commitName) ? "Operation Result" : _commitName` or set `_commitName` from `GetOperationLabel()` when results are computed.

---

## MEDIUM (maintainability and design issues)

### MED-01: Massive Tailwind class strings obliterate readability

Nearly every element has 10+ Tailwind classes. Consider:
```html
class="px-3 py-2 text-lg font-bold font-headline rounded-lg transition-colors hover:bg-surface-container-low"
```
This is repeated (with minor variations) three times in MainLayout for navigation links. Any styling change requires finding and updating all copies.

**Files:** All `.razor` files
**Fix:** Extract repeated patterns to `@apply` directives in `app.css`, or use Blazor CSS isolation with component-scoped styles.

---

### MED-02: MapPage is a god component -- SRP violation

`MapPage.razor` orchestrates: sidebar selection, collection visibility toggling, search query parsing, map initialization, marker selection, POI focus, detail pane open/close, filter chips, and map bounds fitting. It has 13 private fields and 10 methods. This is a page acting as a state manager, an event bus, and a view all at once.

**File:** `Components/Pages/MapPage.razor`
**Fix:** Extract a `MapPageState` service (scoped) or use Fluxor/a mediator pattern. At minimum, extract the filter chip bar into its own component.

---

### MED-03: DataSourcesPage is also a god component

`DataSourcesPage.razor` handles: three different import card types, file upload, URL scraping with progress, collection CRUD, delete confirmation flow, and color selection. 15+ private fields, 8+ methods. Another SRP violation.

**File:** `Components/Pages/DataSourcesPage.razor`
**Fix:** Extract `ImportCard`, `ScrapingPanel`, `FileUploadPanel`, and `SourcesTable` into separate components.

---

### MED-04: No virtualization on any list/table

`PoiTable` caps at 200 rows via `Take(MaxDisplayRows)`. `OperationsPage` caps at 500. But these caps just silently truncate data -- the user has no pagination or scroll-to-load. With 200+ DOM rows, each with multiple elements, the DOM is heavy. The `_Imports.razor` already imports `Microsoft.AspNetCore.Components.Web.Virtualization` but `<Virtualize>` is used nowhere.

**Files:** `PoiTable.razor`, `OperationsPage.razor`
**Fix:** Use `<Virtualize>` component for large lists. Replace hard `Take()` caps with virtualized rendering.

---

### MED-05: `style="text-decoration:none;"` used as inline style on NavLinks

Three NavLink elements in MainLayout use `style="text-decoration:none;"`. This is CSS that should be in a stylesheet, not repeated inline on every nav link.

**File:** `Components/Layout/MainLayout.razor`, lines 10, 15, 21
**Fix:** Add a CSS rule for nav links or use a Tailwind `no-underline` class.

---

### MED-06: Login page uses raw `HttpContext.Request.Query` instead of typed parameter

```csharp
var hasError = HttpContext?.Request.Query.ContainsKey("error") == true;
```

Directly accessing `HttpContext.Request.Query` in a Razor component is fragile and couples the component to HTTP semantics. This only works in static SSR mode. If this page ever becomes interactive, `HttpContext` will be null.

**File:** `Components/Pages/Login.razor`, lines 4-6
**Fix:** Use `[SupplyParameterFromQuery]` attribute on a property: `[SupplyParameterFromQuery] public string? Error { get; set; }`.

---

### MED-07: LoginLayout is a dead-weight component

```razor
@inherits LayoutComponentBase
@Body
```

This layout does literally nothing. It exists only to strip the MainLayout header from the login page. But it adds a component to the render tree for zero benefit. A `@layout` directive pointing at a component that just renders `@Body` is cargo cult architecture.

**File:** `Components/Layout/LoginLayout.razor`
**Fix:** Acceptable if intentional (to avoid the nav header on login). But add a comment explaining why it exists, or merge the login styling into a conditional in MainLayout.

---

### MED-08: `_isSearchActive` field in MapPage is set but under-utilized

`_isSearchActive` is set to `true` in `OnParametersSetAsync` when a search query exists, and to `false` otherwise. It is checked exactly once in `OnAfterRenderAsync` to skip `LoadVisibleCollections`. But it is never used to change the UI rendering. The filter chips, sidebar, and all other elements render identically regardless of search state. The field exists but doesn't drive any meaningful behavior difference.

**File:** `Components/Pages/MapPage.razor`, lines 93, 111, 115
**Fix:** Either use it properly (hide sidebar in search mode, show search results differently) or remove it.

---

### MED-09: Color picker has no accessible name for individual color values

The color picker buttons in DataSourcesPage use `aria-label="Select color #005bbf"`. Hex color codes are meaningless to screen reader users. "#005bbf" conveys nothing about what color it is.

**File:** `Components/Pages/DataSourcesPage.razor`, line 144
**Fix:** Use human-readable color names: `aria-label="Select color blue"`, etc. Map hex codes to names.

---

### MED-10: Hardcoded 50MB upload limit with no server-side enforcement mentioned

```csharp
const long MaxUploadSizeBytes = 50 * 1024 * 1024; // 50MB max
```

The limit is enforced client-side by Blazor's `OpenReadStream`. But if the SignalR hub's `MaximumReceiveMessageSize` is smaller (default 32KB), or if Kestrel's `MaxRequestBodySize` doesn't match, the real limit is whatever is lowest. These must be coordinated.

**File:** `Components/Pages/DataSourcesPage.razor`, line 354
**Fix:** Ensure `Program.cs` configures matching limits for SignalR and Kestrel.

---

## LOW (polish and minor issues)

### LOW-01: Missing `<meta>` description and favicon -- App.razor

No `<meta name="description">`, no `<link rel="icon">`. Basic HTML hygiene missing.

**File:** `Components/App.razor`

---

### LOW-02: `FocusOnNavigate` selector targets `h1` but not all pages have visible `h1`

Routes.razor uses `<FocusOnNavigate Selector="h1" />`. The OperationsPage and DataSourcesPage use `<h2>` as their top heading, not `<h1>`. MapPage has no heading at all in its rendered content. Focus management will silently fail on most pages.

**File:** `Components/Routes.razor`, line 4
**Fix:** Ensure every page has an `<h1>` (even if `sr-only`), or change the selector.

---

### LOW-03: `logout` link in MainLayout has no confirmation

```html
<a href="/logout" class="..." aria-label="Logout">
```

Clicking logout navigates immediately with no confirmation. Easy to accidentally click.

**File:** `Components/Layout/MainLayout.razor`, line 38

---

### LOW-04: `Array.Empty<T>()` vs `new List<T>()` inconsistency for initial values

MapPage initializes collections with `Array.Empty<PoiCollection>()`, while DataSourcesPage and OperationsPage use `new List<PoiCollection>()`. Inconsistent initialization for the same logical concept.

**Files:** `MapPage.razor:86-87`, `DataSourcesPage.razor:271`, `OperationsPage.razor:293`

---

### LOW-05: `PoiDetailPane` accepts nullable `Poi?` but is wrapped in `@if (Poi != null)` guard

The component itself renders nothing when `Poi` is null (line 3). But this means the parent (`MapPage`) ALSO guards with `@if (_selectedPoi != null)` (line 75). Double null-checking across component boundaries is redundant and makes ownership of the null-check unclear.

**File:** `Components/Shared/PoiDetailPane.razor:3`, `Components/Pages/MapPage.razor:75`
**Fix:** Pick one boundary to own the null check. Either the parent conditionally renders the component, or the component handles null internally. Not both.

---

### LOW-06: Settings button in MainLayout is non-functional

```html
<button class="..." aria-label="Settings">
    <span class="material-symbols-outlined ...">settings</span>
</button>
```

This button does nothing. No `@onclick`, no `href`. It's a dead UI element that misleads users.

**File:** `Components/Layout/MainLayout.razor`, lines 35-37

---

### LOW-07: No ARIA live region for loading states

Loading spinners in MapPage, OperationsPage, and DataSourcesPage are purely visual. Screen reader users get no notification when content finishes loading.

**Fix:** Add `aria-live="polite"` to loading/result containers.

---

### LOW-08: `_mapElementId` generates a new GUID on every component instance

```csharp
private string _mapElementId = $"leaflet-map-{Guid.NewGuid():N}";
```

This is fine for uniqueness but wasteful. A simple static counter or the component's hash code would suffice and be more debuggable.

**File:** `Components/Shared/LeafletMap.razor`, line 11

---

### LOW-09: OperationsPage tolerance slider uses `@bind` with `oninput` but doesn't re-run the operation

Changing the slider updates `_toleranceMeters` but does not automatically re-run the active operation. The user changes tolerance, sees the label update, but the results table remains stale. There is no "Re-run" button or auto-recompute.

**File:** `Components/Pages/OperationsPage.razor`, lines 137-138
**Fix:** Either auto-run on tolerance change (with debounce) or add a visible "Re-run with new tolerance" button.

---

### LOW-10: MainLayout error boundary does not log the exception

```html
<ErrorContent Context="ex">
```

The `ex` variable is captured but never logged. The user sees "Something went wrong" but the exception vanishes into the void. No `ILogger`, no `Debug.WriteLine`, nothing.

**File:** `Components/Layout/MainLayout.razor`, lines 50-60
**Fix:** Inject `ILogger` and log `ex` in the error content rendering path (or in `RecoverFromError`).

---

### LOW-11: Routes.razor NotFound page has hardcoded text with no localization hooks

"Page not found" and "The page you are looking for does not exist." are hardcoded English strings. Same issue throughout all components. No resource files, no localization infrastructure.

**File:** `Components/Routes.razor`, lines 11-13

---

### LOW-12: `CollectionSidebar` keyboard handler only supports Enter and Space

The `HandleKeyDown` method only handles `Enter` and `Space`. Arrow key navigation between list items (standard listbox pattern per WAI-ARIA) is not implemented.

**File:** `Components/Shared/CollectionSidebar.razor`, lines 85-91

---

---

## Summary Table

| ID | Severity | Component | Issue |
|---------|----------|-------------------------------|-----------------------------------------------|
| CRIT-01 | CRITICAL | App.razor | CDN assets without SRI -- supply chain risk |
| CRIT-02 | CRITICAL | App.razor | External Google Fonts with no fallback |
| CRIT-03 | CRITICAL | 3 files | `GetGoogleMapsUrl` copy-pasted 3 times |
| CRIT-04 | CRITICAL | LeafletMap.razor | DisposeAsync never destroys JS map object |
| CRIT-05 | CRITICAL | DataSourcesPage.razor | No IAsyncDisposable -- scrape leaks on navigate |
| CRIT-06 | CRITICAL | OperationsPage.razor | No IAsyncDisposable -- operation leaks |
| HIGH-01 | HIGH | MapPage.razor | Search runs on every param change, no debounce |
| HIGH-02 | HIGH | MainLayout.razor | Search form does full page reload |
| HIGH-03 | HIGH | DataSourcesPage, OperationsPage | Mutable List instead of IReadOnlyList |
| HIGH-04 | HIGH | PoiTable.razor | EventCallback invoked without error handling |
| HIGH-05 | HIGH | MapPage.razor | Mutable entity used as UI toggle state |
| HIGH-06 | HIGH | LeafletMap.razor | async void with Debug.WriteLine error handling |
| HIGH-07 | HIGH | OperationsPage.razor | Export uses empty string title, not null |
| MED-01 | MEDIUM | All files | Massive inline Tailwind classes |
| MED-02 | MEDIUM | MapPage.razor | God component -- SRP violation |
| MED-03 | MEDIUM | DataSourcesPage.razor | God component -- SRP violation |
| MED-04 | MEDIUM | PoiTable, OperationsPage | No virtualization despite importing Virtualize |
| MED-05 | MEDIUM | MainLayout.razor | Inline `text-decoration:none` styles |
| MED-06 | MEDIUM | Login.razor | Raw HttpContext.Request.Query access |
| MED-07 | MEDIUM | LoginLayout.razor | Empty layout component with no explanation |
| MED-08 | MEDIUM | MapPage.razor | `_isSearchActive` field unused in UI |
| MED-09 | MEDIUM | DataSourcesPage.razor | Color hex codes as aria-labels |
| MED-10 | MEDIUM | DataSourcesPage.razor | 50MB upload limit not coordinated with server |
| LOW-01 | LOW | App.razor | Missing meta description and favicon |
| LOW-02 | LOW | Routes.razor | FocusOnNavigate targets h1, pages use h2 |
| LOW-03 | LOW | MainLayout.razor | Logout link with no confirmation |
| LOW-04 | LOW | Multiple | Array.Empty vs new List inconsistency |
| LOW-05 | LOW | PoiDetailPane + MapPage | Double null-check across boundary |
| LOW-06 | LOW | MainLayout.razor | Settings button is non-functional |
| LOW-07 | LOW | Multiple | No ARIA live regions for loading states |
| LOW-08 | LOW | LeafletMap.razor | GUID for element ID is overkill |
| LOW-09 | LOW | OperationsPage.razor | Tolerance slider doesn't re-run operation |
| LOW-10 | LOW | MainLayout.razor | Error boundary swallows exception without logging |
| LOW-11 | LOW | Routes.razor | Hardcoded English strings, no localization |
| LOW-12 | LOW | CollectionSidebar.razor | Keyboard nav missing arrow key support |

---

## Elegance Score: 4/10

The fix cycle addressed the obvious crashers -- `ErrorBoundary` was added, `IAsyncDisposable` appeared on MapPage, `IReadOnlyList` was adopted in some places. Good. But the fixes were applied inconsistently: MapPage got IAsyncDisposable while DataSourcesPage and OperationsPage (which need it just as badly) did not. IReadOnlyList was used in MapPage but not in the other two pages. `GetGoogleMapsUrl` was annotated with a "DRY" comment and then duplicated three times. The codebase reads like three developers each applied half the review feedback to their own file and never spoke to each other.

The god-component problem (MapPage at 223 lines, DataSourcesPage at 443 lines, OperationsPage at 425 lines) is the architectural debt that will slow every future feature. These pages are doing the work of 3-4 components each.

On the positive side: accessibility is better than average (aria-labels, roles, keyboard handling exists even if incomplete), the loading states are present, and the error boundary actually works. The code is readable despite the Tailwind noise. It is not bad code. It is inconsistent code, which is worse, because inconsistent code teaches new developers that the patterns are optional.
