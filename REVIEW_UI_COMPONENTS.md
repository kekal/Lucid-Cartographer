# UI Components Code Review -- Lucid Cartographer

**Reviewer:** Principal Engineer (grumpy)
**Date:** 2026-04-12
**Scope:** All 11 Blazor UI component files under `Components/`
**Verdict:** Functional prototype with systemic quality gaps. Not production-ready.

---

## CRITICAL

### CRIT-01: CDN Tailwind in production (`App.razor:7`)
```
<script src="https://cdn.tailwindcss.com"></script>
```
The Tailwind CDN script is a **development-only** tool. It ships the entire Tailwind runtime to every client, bloats initial payload, causes FOUC (flash of unstyled content), and will break under CSP policies. Must use a build-time Tailwind compile step that produces a purged CSS file. This alone disqualifies the app from any serious deployment.

### CRIT-02: Unpinned CDN dependencies with no SRI (`App.razor:10-11`)
Leaflet is loaded from `unpkg.com` with no `integrity` attribute and no version lock beyond `@1.9.4`. A CDN compromise delivers arbitrary JS into every session. All CDN scripts must have `integrity` and `crossorigin` attributes, or better yet, be self-hosted.

### CRIT-03: `async void` event handler in LeafletMap (`LeafletMap.razor:60`)
```csharp
private async void HandleMarkerClicked(int poiId)
```
`async void` swallows exceptions. If `OnMarkerSelected.InvokeAsync` throws, the exception vanishes into the void -- no logging, no error boundary, nothing. The user sees a frozen UI with zero feedback. This must be `async Task` with the event delegate signature changed, or the body must be wrapped in try/catch with explicit error reporting.

### CRIT-04: No error boundaries anywhere
Not a single `<ErrorBoundary>` in the entire component tree. `MainLayout.razor` wraps `@Body` with zero protection. Any unhandled exception during render in MapPage, DataSourcesPage, or OperationsPage nukes the entire circuit. At minimum, MainLayout must wrap Body:
```razor
<ErrorBoundary @ref="errorBoundary">
    <ChildContent>@Body</ChildContent>
    <ErrorContent>...recovery UI...</ErrorContent>
</ErrorBoundary>
```

### CRIT-05: `Task.Delay(200)` as synchronization mechanism (`MapPage.razor:106`)
```csharp
await Task.Delay(200);
_mapLoaded = true;
```
A fixed 200ms delay to "ensure map is initialized" is a race condition waiting to happen. On slow machines, cold starts, or high-latency circuits this will fail silently. The JS interop `InitMapAsync` should return a completion signal (a promise that resolves when the map fires its `load` event). Arbitrary delays are never acceptable synchronization.

### CRIT-06: No `IDisposable` / `IAsyncDisposable` on MapPage (`MapPage.razor`)
MapPage holds a `@ref` to `LeafletMap` and subscribes to `NavigationManager` URI changes implicitly via `OnParametersSetAsync`. It never disposes the LeafletMap reference or cleans up. If the user navigates away and back repeatedly, orphaned JS map instances accumulate. MapPage should implement `IAsyncDisposable` and call into JS to destroy the map instance.

---

## HIGH

### HIGH-01: Mutable list parameters passed by reference (`MapPage.razor` -> children)
`_collections` and `_visiblePois` are `List<T>` fields mutated in-place (`_visiblePois.Clear()`, `_visiblePois.AddRange(...)` on line 121-127) and passed as `[Parameter]` to child components. Blazor's change detection compares reference equality for parameters -- mutating the same list instance means child components may not re-render when contents change, or worse, may re-render at wrong times. Use immutable snapshots: assign a new `List<T>` or `IReadOnlyList<T>` each time.

### HIGH-02: `StateHasChanged()` called excessively and inconsistently (`MapPage.razor`)
`StateHasChanged()` is called explicitly in `LoadVisibleCollections` (line 134), `HandleCollectionSelected` (line 153), `SelectPoi` (line 187), and `CloseDetailPane` (line 194). Most of these are already inside `async Task` event handlers, which trigger re-render automatically. The manual calls are either redundant (wasting render cycles) or masking the mutable-list problem from HIGH-01. Fix the state management and remove the redundant calls.

### HIGH-03: Delete without confirmation (`DataSourcesPage.razor:394-398`, `OperationsPage.razor:341-343`)
`DeleteCollection` immediately deletes with zero confirmation dialog. One mis-click destroys an entire dataset. `DiscardPoi` similarly has no undo. Destructive operations require confirmation.

### HIGH-04: No loading state on initial data fetch (`MapPage.razor`, `OperationsPage.razor`)
`OnInitializedAsync` fetches collections but the template renders immediately with an empty list. There is no skeleton/spinner shown while data loads. The user sees a blank sidebar for the entire duration of the first database round-trip. Every page that fetches data in `OnInitializedAsync` must have a corresponding loading state.

### HIGH-05: Google Maps URL constructed via string interpolation -- injection risk (`PoiTable.razor:71`, `PoiDetailPane.razor:158`, `OperationsPage.razor:232`)
```
$"https://www.google.com/maps/search/?api=1&query={poi.Latitude},{poi.Longitude}"
```
This pattern is repeated in three components. Duplicated logic, and if `Latitude`/`Longitude` ever contain unexpected values (NaN, Infinity), the URL is malformed. Extract to a shared utility method with validation.

### HIGH-06: Scraper progress callback invokes `StateHasChanged` without await (`DataSourcesPage.razor:363`)
```csharp
InvokeAsync(StateHasChanged);
```
The `Task` returned by `InvokeAsync` is fire-and-forget here. If the render pipeline throws, the exception is lost. Must `await` the call or at minimum log failures.

### HIGH-07: `LeafletMap` public methods are called without null/init guards from parent (`MapPage.razor`)
Every call site in MapPage checks `_leafletMap != null` but does NOT check `_mapLoaded`. Meanwhile `LeafletMap` internally checks `_initialized`. If the map component exists but JS init hasn't completed, the parent happily calls methods that silently no-op. There is no feedback to the user that their action was ignored. This is a confusing UX failure mode.

### HIGH-08: 50MB file upload buffered entirely in memory (`DataSourcesPage.razor:331-333`)
```csharp
using var stream = file.OpenReadStream(maxAllowedSize: 50 * 1024 * 1024);
using var ms = new MemoryStream();
await stream.CopyToAsync(ms);
```
On Blazor Server, this allocates up to 50MB on the server per upload, per circuit. Multiple concurrent users uploading large files will OOM the server. Stream processing or chunked upload is required.

---

## MEDIUM

### MED-01: Hardcoded magic numbers throughout
| Location | Value | Should be |
|---|---|---|
| `MapPage.razor:106` | `200` (ms delay) | Named constant or removed entirely |
| `DataSourcesPage.razor:331` | `50 * 1024 * 1024` | `MaxUploadSizeBytes` constant |
| `PoiTable.razor:44` | `200` (row limit) | Configurable parameter with default |
| `OperationsPage.razor:214` | `500` (row limit) | Configurable parameter with default |
| `OperationsPage.razor:122` | `10`, `500`, `10` (range slider) | Constants or config |
| `LeafletMap.razor:36` | `16` (default zoom) | Named constant |
| `PoiDetailPane.razor:175` | `35` (URL truncation length) | Named constant |

### MED-02: Color palette hardcoded in two places
`DataSourcesPage.razor:285-288` hardcodes 8 color strings. `App.razor:19-32` hardcodes the same theme colors in the Tailwind config. The color values `#005bbf`, `#006e2c`, `#b81d17` appear in both places with different purposes but no single source of truth. If the palette changes, someone will miss one.

### MED-03: No virtualization on large lists (`PoiTable.razor`, `OperationsPage.razor`)
Both tables use `@foreach` with `.Take(200)` / `.Take(500)` as a crude pagination substitute. The `_Imports.razor` already imports `Microsoft.AspNetCore.Components.Web.Virtualization` but `<Virtualize>` is never used. For datasets of thousands of POIs, this means either truncated results or massive DOM trees. Use `<Virtualize>` with an items provider.

### MED-04: Inline styles that should be CSS classes
| Location | Inline style |
|---|---|
| `MainLayout.razor:12` | `style="text-decoration:none;"` (repeated 3x) |
| `MapPage.razor:50` | `style="border-top: 1px solid #e5e8ee;"` |
| `MapPage.razor:62` | `style="border-left: 1px solid #e5e8ee;"` |
| `MapPage.razor:27` | `style="background-color: @col.Color"` (dynamic -- acceptable but should have fallback) |
| `CollectionSidebar.razor:24` | `style="background-color: @col.Color"` |
| `PoiDetailPane.razor:65,109` | `style="font-variation-settings: 'FILL' @(...)"` |
The hardcoded color `#e5e8ee` matches the `surface-container-highest` token -- use the Tailwind class `border-surface-container-highest` instead.

### MED-05: Dead / unused code
- `MapPage.razor:77`: `_isSearchActive` is set but only read in `OnAfterRenderAsync`. It is never used to conditionally render anything in the template.
- `MapPage.razor:76`: `_mapLoaded` is set to `true` but never set back to `false` and is only checked in `LoadVisibleCollections`. It serves no purpose if the delay hack (CRIT-05) is removed.
- `PoiTable.razor:11-18`: `ShowSortByDistance` parameter and `OnSortByDistanceClicked` callback exist but no parent ever sets `ShowSortByDistance = true`. Dead feature.
- `OperationsPage.razor:285`: `CanRunBinaryOp` property is declared but never referenced anywhere.

### MED-06: No parameter validation on child components
`CollectionSidebar`, `PoiTable`, `PoiDetailPane` accept parameters but never validate them. `PoiTable` will throw `NullReferenceException` if `Pois` is null (the `Pois.Any()` call on line 24). Default `new()` initializers help but any parent passing `null` explicitly crashes the component. Use `[EditorRequired]` on mandatory parameters and add null guards.

### MED-07: Fragile Tailwind class string concatenation
Throughout all components, conditional classes are built via string interpolation:
```razor
@(_activeCard == "file" ? "ring-2 ring-primary" : "")
@(_isDedupMode ? "opacity-40 pointer-events-none" : "")
@(SelectedPoiId == poi.Id ? "bg-surface-container" : "")
```
These are fragile, hard to test, and invisible to Tailwind's purge scanner if the classes only appear inside interpolated strings. A utility like `CssBuilder` or a simple helper method would be more maintainable.

### MED-08: `OperationsPage` is a god component (~388 lines)
This single component handles: collection selection, operation configuration, tolerance slider, operation execution, results display, discard/restore workflow, commit-to-layer dialog, and KML export. That is at least 4-5 responsibilities. Extract: `OperationConfigPanel`, `OperationResultsTable`, `CommitDialog`.

### MED-09: `DataSourcesPage` is also too large (~400 lines)
Handles: import card selection, three different import flows (file, takeout, shared list scraping), file upload, scraper progress, collection management table, and delete. Extract: `ImportCard`, `ScrapeImportForm`, `ManagedSourcesTable`.

### MED-10: EventCallback invocations not wrapped in try/catch (`CollectionSidebar.razor:22,30`)
```razor
@onclick="() => OnCollectionSelected.InvokeAsync(col.Id)"
```
Direct `InvokeAsync` in the template. If the parent's handler throws, the exception propagates up unhandled. All EventCallback invocations should be routed through `@code` methods with try/catch.

---

## LOW

### LOW-01: Accessibility is essentially absent
- **No ARIA labels on interactive elements.** The settings button (MainLayout:35), visibility toggle (CollectionSidebar:28), close buttons (DataSourcesPage:64, PoiDetailPane:9), color picker buttons (DataSourcesPage:142), and operation buttons (OperationsPage:83-113) have zero `aria-label` attributes. Screen readers will announce "button" with no context.
- **No `role` attributes** on the collection sidebar list (should be `role="listbox"` with `role="option"` items).
- **No keyboard navigation.** Collection items, POI table rows, and operation buttons rely entirely on `@onclick`. No `@onkeydown` handlers, no `tabindex`, no focus management.
- **Color-only state indicators.** Collection visibility (filled/unfilled eye icon) and collection color dots communicate meaning through color alone. No text alternative for colorblind users.
- **Missing `<label>` associations.** The search input (MainLayout:32) has no `<label>` element or `aria-label`. The tolerance slider (OperationsPage:122) has a visual label but no `for`/`id` binding.
- **No skip-to-content link.** No way to bypass the header navigation via keyboard.
- **Star ratings are not accessible.** The star loops in PoiDetailPane (lines 60-68, 105-113) produce decorative spans with no `aria-label` communicating the actual rating value.

### LOW-02: No `<title>` element in `App.razor`
The `<head>` contains `<HeadOutlet />` which handles `<PageTitle>`, but there is no fallback `<title>` element. If a page fails to render its `<PageTitle>` component, the browser tab shows no title.

### LOW-03: Search form uses full page navigation (`MainLayout.razor:30`)
```html
<form action="/" method="get" class="relative" data-enhance="false">
```
`data-enhance="false"` forces a full page reload for every search. In a Blazor Server app this destroys the circuit, reconnects, and re-initializes everything. Search should use Blazor's interactive model (bind to a field, debounce, call service).

### LOW-04: Global `downloadFile` JS function pollutes window scope (`App.razor:49-56`)
A bare `function downloadFile(...)` in a `<script>` tag creates a global. Use a namespaced pattern (`window.LucidCartographer.downloadFile`) or a JS module.

### LOW-05: Font loading blocks render (`App.razor:8-9`)
Two Google Fonts stylesheets are loaded synchronously in `<head>`. These are render-blocking. Use `font-display: swap` (partially handled by `&display=swap` in the URL) but also consider `<link rel="preload">` or loading fonts asynchronously.

### LOW-06: `_mapElementId` is a fixed string (`LeafletMap.razor:12`)
```csharp
private string _mapElementId = "leaflet-map";
```
If two `LeafletMap` instances ever coexist (unlikely today, easy to break tomorrow), they collide on the same DOM ID. Generate a unique ID: `$"leaflet-map-{Guid.NewGuid():N}"`.

### LOW-07: Date formatting is not locale-aware
`col.CreatedDate.ToString("MMM dd, yyyy")` (DataSourcesPage:234), `poi.AddedDate.ToString("MMM dd, yyyy")` (PoiTable:69), `poi.AddedDate.ToString("MMMM dd, yyyy")` (PoiDetailPane:148) all use US-centric date formats. For an international mapping tool, this should respect `CultureInfo` or at least be consistent (short vs long month name varies between components).

### LOW-08: `PoiDetailPane.TruncateUrl` is naive (`PoiDetailPane.razor:173-176`)
Strips `https://` and `http://` via string replace but does not handle `ftp://`, protocol-relative URLs, or query strings that make URLs long. Use `Uri` class for proper parsing.

### LOW-09: `Routes.razor` NotFound page has no navigation guidance
The 404 page shows "Page not found" with an icon but offers no link back to the home page or any actionable next step.

### LOW-10: Repeated Google Maps URL template
The URL pattern `https://www.google.com/maps/search/?api=1&query={lat},{lng}` appears in `PoiTable.razor:71`, `PoiDetailPane.razor:158`, and `OperationsPage.razor:232`. DRY violation. Extract to a static helper.

### LOW-11: No `@key` directives on `@foreach` loops
`CollectionSidebar.razor:18`, `PoiTable.razor:44`, `OperationsPage.razor:214`, `DataSourcesPage.razor:223`, `MapPage.razor:24` -- none of the `@foreach` loops use `@key`. Blazor will diff by index rather than identity, causing incorrect DOM reuse when lists are reordered, filtered, or items removed.

### LOW-12: Import result feedback disappears on card switch (`DataSourcesPage.razor:300-306`)
Calling `ShowUpload()` resets `_importResult` and `_importError`. If the user just finished an import and clicks a different card, they lose confirmation of what happened. The success/error state should persist independently of the active card.

---

## SUMMARY

| Severity | Count |
|----------|-------|
| CRITICAL | 6 |
| HIGH | 8 |
| MEDIUM | 10 |
| LOW | 12 |
| **Total** | **36** |

The codebase reads like a fast prototype that shipped before anyone reviewed it. The CDN Tailwind, the `async void`, the `Task.Delay` synchronization, and the total absence of error boundaries are the showstoppers. Fix those before adding any new features. The accessibility gaps alone would fail any audit. The god-component pages need decomposition before they grow further.
