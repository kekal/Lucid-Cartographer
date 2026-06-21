namespace LucidCartographer.Services;

/// <summary>
/// Centralized UI string constants.
/// </summary>
public static class UiStrings
{
    // Common
    public const string AppTitle = "Lucid Cartographer";
    public const string Loading = "Loading...";
    public const string LoadingCollections = "Loading collections...";
    public const string SomethingWentWrong = "Something went wrong";
    public const string UnexpectedError = "An unexpected error occurred. Please try again.";
    public const string TryAgain = "Try Again";
    public const string Close = "Close";
    public const string Back = "Back";
    public const string Cancel = "Cancel";
    public const string Save = "Save";
    public const string Yes = "Yes";
    public const string No = "No";
    public const string Rename = "Rename";
    public const string Logout = "Logout";
    public const string SearchPois = "Search POIs...";
    public const string Settings = "Settings";

    // Navigation
    public const string NavMap = "Map";
    public const string NavDataSources = "Data Sources";
    public const string NavOperations = "Operations";
    public const string NavMore = "More";
    public const string NavSources = "Sources";

    // Map Page
    public const string MapPageTitle = "Map - Lucid Cartographer";
    public const string FitAll = "Fit All";
    public const string FitMapToVisible = "Fit map to all visible collections";
    public const string Labels = "Labels";
    public const string ToggleLabels = "Show or hide POI name labels on the map";
    public const string ShowMyLocation = "Show my location";
    public const string LocationUnavailable = "Couldn't get your location. Allow location access for this site and make sure GPS is on.";
    public const string FilteredResults = "Filtered Results";
    public const string NoPoiToDisplay = "No POIs to display. Toggle collection visibility or import data.";
    public const string LocationDetails = "Location Details";
    public const string Collection = "Collection";
    public const string CloseDetailPane = "Close detail pane";
    public const string OpenInGoogleMaps = "Open in Google Maps";
    public const string EnrichDetails = "Enrich Details";
    public const string EnrichDetailsAria = "Re-fetch address, website, phone, and image for this location";
    public const string ManualEnrich = "Manual Enrich";
    public const string ManualEnrichAria = "Paste a Google Maps URL to enrich this location manually";
    public const string UseGoogleMapsName = "Use the name from Google Maps";
    public const string EnrichFallbackTitle = "Couldn't find details for {0}";
    public const string EnrichFallbackBody = "Open Google Maps, locate the correct place, then paste its URL below to enrich this POI.";
    public const string EnrichFallbackSearchLink = "Search on Google Maps";
    public const string EnrichFallbackUrlLabel = "Google Maps place URL";
    public const string EnrichFallbackInvalidUrl = "Please paste a Google Maps URL (google.com/maps/... or maps.app.goo.gl).";
    public const string Collections = "Collections";
    public const string NoCollectionsYet = "No collections yet.";
    public const string ImportDataHint = "Import data via Data Sources tab.";

    // Trip View
    public const string TripView = "Trip View";
    public const string TripViewToggleAria = "Toggle Trip View — order the visible places into a trip";
    public const string TripViewEnabledAnnouncement = "Trip View on. Places are ordered as a trip.";
    public const string TripViewDisabledAnnouncement = "Trip View off. Showing the plain collection.";
    // Announced when Trip View disables itself if fewer than two placeable stops remain.
    public const string TripViewAutoDisabledAnnouncement =
        "Trip View off — fewer than two placeable stops remain.";
    // {0} = stop number; used as the order badge's screen-reader label.
    public const string StopOrderBadgeAria = "Stop {0}";

    // Trip View — stop-list panel
    public const string TripStopList = "Trip stops";
    public const string TripStopListAria = "Trip stop list, in travel order";
    // {0} = stop count; announced via the panel's aria-live region.
    public const string TripStopCountAria = "{0} stop trip";
    public const string TripStopListEmpty = "No placeable stops yet.";
    // Wide trip-table row Actions: Focus on map + Open in Google Maps. {0} = POI name.
    public const string TripFocusOnMap = "Focus on map";
    public const string TripFocusOnMapAria = "Focus map on {0}";
    public const string TripOpenInGoogleMaps = "Open in Google Maps";
    public const string TripOpenInGoogleMapsAria = "Open {0} in Google Maps";
    // Enrichment-state icon titles for the Name column (error/red, location_on/muted, hourglass_empty/amber).
    public const string TripEnrichmentFailed = "Enrichment failed — paste a Google Maps URL";
    public const string TripEnrichmentEnriched = "Enriched";
    public const string TripEnrichmentWaiting = "Waiting for enrichment";
    // {0} = this stop's number, {1} = total stops. Row-level screen-reader label.
    public const string TripStopBadgeAria = "Stop {0} of {1}";
    // Dwell-minutes input on each stop row. {0} = the stop's POI name.
    public const string TripDwellPlaceholder = "min";
    // Desktop dwell control uses native HH:MM duration picker; mobile keeps minutes input.
    public const string TripDwellHhmmPlaceholder = "hh:mm";
    public const string TripDwellAria = "Dwell time in minutes at {0}";
    public const string TripTimelinePlaceholder = "—";
    public const string TripTimelineAria = "Arrival time";

    // Trip View — list ↔ map selection sync. {0} = stop number, {1} = POI name; announced via aria-live.
    public const string TripStopSelectedAnnouncement = "Selected stop {0}: {1}";
    // Selectable stop-row accessible name. {0} = stop number, {1} = total stops,
    // {2} = POI name.
    public const string TripStopRowAria = "Stop {0} of {1}: {2}";

    // Trip View — stop reorder. {0} = POI name for the aria-labels and post-move announcement.
    public const string TripMoveStopUp = "Move {0} up";
    public const string TripMoveStopDown = "Move {0} down";
    // {0} = POI name, {1} = new stop number, {2} = total stops.
    public const string TripStopMovedAnnouncement = "{0} moved to stop {1} of {2}";
    // Drag-handle accessible name. {0} = POI name.
    public const string TripDragHandle = "Drag to reorder {0}";

    // Trip View — Start/Finish designation. Set/unset control labels ({0} = POI name),
    // badge/marker accessible names, and aria-live announcements.
    public const string TripSetAsStart = "Set {0} as start";
    public const string TripSetAsFinish = "Set {0} as finish";
    public const string TripUnsetStart = "Unset {0} as start";
    public const string TripUnsetFinish = "Unset {0} as finish";
    // {0} = total stops. The Start badge's screen-reader label (Start is always stop 1).
    public const string TripStartBadgeAria = "Start — stop 1 of {0}";
    // {0} = total stops (the Finish is always the last stop, N of N).
    public const string TripFinishBadgeAria = "Finish — stop {0} of {0}";
    // {0} = POI name. Map-marker accessible name/title for the pinned roles.
    public const string TripStartMarkerAria = "Start: {0}";
    public const string TripFinishMarkerAria = "Finish: {0}";
    // {0} = POI name. Announced after a Start designation or when it is cleared.
    public const string TripStartSetAnnouncement = "{0} set as start — stop 1";
    public const string TripStartClearedAnnouncement = "Start cleared";
    // Shape announcements: clearing the Finish restores the Roundtrip; a distinct Finish opens the path.
    public const string TripRoundtripAnnounce = "Roundtrip — returns to start";
    public const string TripOpenPathAnnounce = "Open path — ends at {0}";

    // Trip View — routing-data attribution. Shown on map attribution when OSM-based routing (OSRM) is active.
    public const string TripRoutingAttributionOsm =
        "Routing &copy; OSRM &middot; Map data &copy; OpenStreetMap contributors (ODbL)";

    // Trip View — unplaceable stops. POI kept in collection, excluded from route.
    public const string TripStopNotPlaceable = "Not placeable";
    public const string TripStopNotPlaceableDetail = "Not placeable — no coordinates. Kept in the collection, excluded from the route.";
    public const string TripStopNotPlaceableAria = "Not placeable: this stop has no coordinates and is excluded from the route, but kept in the collection.";

    // Trip View — per-leg travel time + Fidelity badge. Times/distances formatted at UI edge; "—" marks uncomputed legs.
    public const string TripLegTimeUnknown = "—";
    // {0} = formatted travel time (e.g. "1h 20m"). Per-leg time accessible name.
    public const string TripLegTravelTimeAria = "Travel time {0}";
    // {0} = formatted distance (e.g. "12 km"). Per-leg distance accessible name.
    public const string TripLegDistanceAria = "Distance {0}";
    // Fidelity badge visible text + accessible names. Placeholder/null never renders.
    public const string TripFidelityMeasured = "Measured";
    public const string TripFidelityEstimated = "Estimated";
    public const string TripFidelityManual = "Manual";
    // {0} = the fidelity word. Badge accessible name conveying provenance.
    public const string TripFidelityAria = "Provenance: {0}";
    // Fidelity badge tooltips: plain-language explanation for both title and aria-label (parity).
    public const string TripFidelityEstimatedTooltip = "Estimated — straight-line approximation, not road distance";
    public const string TripFidelityMeasuredTooltip = "Measured — real road route.";
    public const string TripFidelityManualTooltip = "Manual — you entered this time.";
    // Announced via aria-live while one or more legs are still computing.
    public const string TripLegComputingAnnouncement = "Computing travel times…";
    public const string TripLegComputingAria = "Travel time computing";
    // Trip total travel time. {0} = formatted total; the label introduces the value.
    public const string TripTotalTravelTimeLabel = "Total travel time";
    public const string TripTotalTravelTimeAria = "Total travel time {0}";
    // Explicit "Recompute travel times" control; invalidates eligible cached legs and re-requests them.
    public const string TripRecomputeLabel = "Recompute travel times";
    public const string TripRecomputeAria = "Recompute travel times";
    // Explicit "Sort in Traveling Salesman order" control; reorders placeable stops into an efficient loop. {0} = stop count.
    public const string TripSortTspLabel = "Sort in Traveling Salesman order";
    public const string TripSortTspAria = "Sort stops in Traveling Salesman order";
    public const string TripSortTspAnnouncement = "Stops sorted into travel order ({0} stops)";
    // UI-edge duration/distance format patterns. Unit words and sub-minute/zero tokens are localizable.
    // Minute unit reads "min" (not "m") to disambiguate from distance meters.
    public const string TripDurationHoursMinutes = "{0}h {1} min";
    public const string TripDurationMinutes = "{0} min";
    public const string TripDurationSubMinute = "<1 min";
    public const string TripDurationZero = "0 min";
    public const string TripDistanceKilometers = "{0:0.#} km";
    public const string TripDistanceMeters = "{0:0} m";
    // Accessible name for the leg-time slot of a stop with NO departing leg (last stop of open path or unplaceable row).
    public const string TripLegNoTravelTimeAria = "No travel time";

    // Honest approximate note shown when routing engine was unreachable; only on fallback trips, not on shipping default.
    public const string TripApproximateEstimatesNote =
        "Couldn't reach the routing engine — showing straight-line estimates.";

    // Contextual note on default deployment (no measured provider configured) explaining why all legs are estimates.
    public const string TripMockEstimateNote =
        "All times are straight-line estimates. Enable OSRM for measured road times.";
    public const string TripMockEstimateOsrmLink = "How to enable OSRM";
    public const string TripMockEstimateOsrmHref = "docs/osrm.md";

    // Trip View — travel-mode selector. Segment labels + radiogroup accessible name; per-trip choice.
    public const string TripTravelModeSelectorAria = "Travel mode";
    public const string TripTravelModeAnyAir = "Any/Air";
    public const string TripTravelModeDrive = "Drive";
    public const string TripTravelModeWalk = "Walk";
    public const string TripTravelModeCycle = "Cycle";

    // Trip View — per-leg mode pill. Shows leg's mode or "Any — set mode" outline pill (not error tone). {0} = origin stop name.
    public const string TripLegModeAnySetMode = "Any — set mode";
    public const string TripLegModePillAria = "Travel mode for the leg departing {0}";
    public const string TripLegModePillTitle = "Set the travel mode for the leg departing {0}";
    public const string TripLegModeMenuAria = "Choose travel mode for the leg departing {0}";

    // Trip View — bulk leg-mode selector. Assigns one travel mode to ALL legs; overwrite toggle (default = fill undefined legs only).
    public const string TripBulkModeLabel = "Set mode for all…";
    public const string TripBulkModeAria = "Set travel mode for all legs";
    public const string TripBulkModeMenuAria = "Choose a travel mode to apply to all legs";
    public const string TripBulkModeOverwriteLabel = "Overwrite legs that already have a mode";
    public const string TripBulkModeOverwriteAria = "Overwrite legs that already have a mode";

    // Trip View — manual Any/Air leg time. Numeric minutes input for Any/Air legs; entering a value carries Fidelity.Manual. {0} = origin stop name.
    public const string TripManualMinutesPlaceholder = "min";
    public const string TripManualMinutesAria = "Manual travel time in minutes for the leg departing {0}";
    // Click-to-edit affordance on connector's travel time (available on ANY leg). {0} = origin stop name.
    public const string TripLegEditTimeAria = "Edit travel time for the leg departing {0}";
    // Inter-row leg connector's reset (↺) affordance: reverts a Manual leg override back to the computed time. {{0}} = origin stop name.
    public const string TripLegResetManualAria = "Reset manual travel time for the leg departing {0}";

    // Trip View — itinerary timeline. Per-stop arrival = relative cumulative offset + optional wall-clock time + lowest fidelity.
    // Em-dash marks genuinely unknown arrival; all copy is culture-formatted at UI edge only.
    // {0} = the formatted relative offset (e.g. "+2h 15m"). The offset is always shown.
    public const string TripTimelineOffset = "+{0}";
    // {0:HH:mm} = the wall-clock arrival; shown only when a TripStartTime is set.
    public const string TripTimelineWallClock = "{0:HH:mm}";
    // Wall-clock arrival on a LATER calendar day shows its DATE alongside the time. {{0:d}} = locale short date, {{1:HH:mm}} = time (24h format).
    public const string TripTimelineWallClockWithDate = "{0:d} {1:HH:mm}";
    // {{0}} = the wall-clock arrival, {{1}} = the qualifier word (e.g. "Estimated"). Renders "~14:10 · Estimated" for estimated arrivals.
    public const string TripTimelineEstimatedPrefix = "~{0}";
    public const string TripTimelineQualified = "{0} · {1}";
    // The unknown-arrival marker (an upstream leg's duration was unknown).
    public const string TripTimelineUnknown = "—";
    // Per-stop arrival accessible name. {0} = the arrival text (offset and/or wall-clock,
    // qualified, or the unknown marker).
    public const string TripTimelineArrivalAria = "Arrival {0}";
    // The finish/return readout at the end of the list. Label + {{0}} = arrival text.
    public const string TripTimelineFinishLabel = "Return to start";
    public const string TripTimelineFinishOpenLabel = "Finish";
    public const string TripTimelineFinishAria = "Trip ends at {0}";
    // Trip start time input (header). Label + accessible name; null means relative offsets only.
    public const string TripStartTimeLabel = "Start time";
    public const string TripStartTimeAria = "Trip start time (wall-clock)";
    // Time-budget input (header). Label + accessible name; minutes. Mobile still uses these raw-minutes strings.
    public const string TripBudgetLabel = "Time budget";
    public const string TripBudgetAria = "Time budget in minutes";
    public const string TripBudgetPlaceholder = "min";
    // Soft (amber, never red) overrun flag, shown only when the KNOWN total exceeds the set budget.
    public const string TripBudgetOverrunLabel = "Over budget";
    public const string TripBudgetOverrunAria = "The trip total exceeds the time budget";

    // "Time limit" rename for desktop. Offers two ways to set canonical TimeBudgetMinutes: HH:MM duration (≤24h) and finish-by deadline (any horizon).
    // Label/aria/placeholder below replace old "Time budget" copy on desktop; overrun chip reads "Over limit".
    public const string TripTimeLimitLabel = "Time limit";
    public const string TripTimeLimitAria = "Time limit as a duration (HH:MM)";
    public const string TripTimeLimitPlaceholder = "hh:mm";
    // Aria/title for the shared DurationInput ▲▼ steppers (Trip stops compaction).
    public const string TripDurationStepUpAria = "Increase duration";
    public const string TripDurationStepDownAria = "Decrease duration";
    // Finish-by deadline alternative (a TIME GOAL, computed once into minutes; distinct from Finish STOP). Requires a start.
    public const string TripFinishByLabel = "Finish by";
    public const string TripFinishByAria = "Finish-by deadline (date and time); requires a start time";
    // Shown when no start time is set: the deadline input needs a start to compute against.
    public const string TripFinishByNeedsStartHint = "Set a start time to use a finish-by deadline";
    // Soft (amber, never red) over-limit flag (rename of the overrun chip).
    public const string TripOverLimitLabel = "Over limit";
    public const string TripOverLimitAria = "The trip total exceeds the time limit";

    // Data Sources Page
    public const string DataSourcesPageTitle = "Data Sources - Lucid Cartographer";
    public const string DataAndImports = "Data & Imports";
    public const string DataAndImportsDesc = "Import geospatial datasets and manage your location data sources.";
    public const string KmlGpxUpload = "KML/GPX Upload";
    public const string KmlGpxUploadDesc = "Import GPX or KML files from browser extensions, Google My Maps exports, or other tools.";
    public const string GoogleTakeout = "Google Takeout";
    public const string GoogleTakeoutDesc = "Import your saved places from Google Takeout GeoJSON export.";
    public const string SharedGoogleList = "Shared Google List";
    public const string SharedGoogleListDesc = "Paste a Google Maps list URL \u2014 the app scrapes all places automatically.";
    public const string CollectionName = "Collection Name";
    public const string Color = "Color";
    public const string ImportComplete = "Import complete";
    public const string ImportFailed = "Import failed";
    public const string ManagedSources = "Managed Sources";
    public const string ResetFailedEnrichment = "Reset failed enrichment";
    public const string FailedEnrichmentReset = "Reset {0} failed enrichment item(s).";
    public const string DatabaseMaintenance = "Database maintenance";
    public const string DeduplicateDatabase = "Deduplicate database";
    public const string Deduplicating = "Deduplicating…";
    public const string DeduplicateDone = "Merged {0} duplicate POI(s) across {1} place group(s).";
    public const string DeduplicateNone = "No duplicates found — database is clean.";
    public const string DeduplicateFailed = "Deduplication failed: {0}";
    public const string NoDatasetsYet = "No datasets imported yet. Use the import cards above to get started.";
    public const string CloseImportPanel = "Close import panel";
    public const string ImportFile = "Import File";
    public const string ImportFromTakeout = "Import from Google Takeout";
    public const string ImportSharedList = "Import Shared Google Maps List";
    public const string FetchMyLists = "Fetch My Lists";
    public const string FetchingLists = "A browser window has opened. Sign in to Google if prompted, then return here.";
    public const string ImportSelectedLists = "Import Selected";
    public const string NoSavedListsFound = "No saved lists found in this Google account.";
    public const string SelectListsToImport = "Select lists to import:";
    public const string GoogleProfileActive = "Google profile active";
    public const string ResetProfile = "Reset";
    public const string NoGoogleProfile = "No Google profile. You'll be prompted to sign in.";

    // Operations Page
    public const string OperationsPageTitle = "Operations - Lucid Cartographer";
    public const string SourceSelection = "Source Selection";
    public const string SourceDatasetA = "Source Dataset A";
    public const string SourceDatasetB = "Source Dataset B";
    public const string NotUsedForDedup = "not used for Dedup";
    public const string SelectCollection = "Select collection...";
    public const string SelectBFirst = "Select Source B first";
    public const string LogicalOperations = "Logical Operations";
    public const string SpatialTolerance = "Spatial Tolerance";
    public const string ResultPreview = "Result Preview";
    public const string ExportResult = "Export Result";
    public const string CommitToLayer = "Commit to Layer";
    public const string SaveAsNewCollection = "Save as new collection";
    public const string ProcessingOperation = "Processing operation...";
    public const string SelectAndRun = "Select datasets and run an operation";
    public const string SelectAndRunHint = "Choose collections A and B, then click an operation button.";
    public const string Difference = "Difference";
    public const string Intersection = "Intersection";
    public const string Union = "Union";
    public const string Deduplicate = "Deduplicate";
    public const string Discard = "Discard";
    public const string Restore = "Restore";

    // Not Found
    public const string PageNotFound = "Page not found";
    public const string PageNotFoundDesc = "The page you are looking for does not exist.";
    public const string GoToMap = "Go to Map";

    // Login
    public const string EnterCredentials = "Enter your username and password to continue";
    public const string Username = "Username";
    public const string Password = "Password";
    public const string Login = "Login";
    public const string IncorrectCredentials = "Incorrect username or password.";

    // Error
    public const string ErrorTitle = "Error.";
}
