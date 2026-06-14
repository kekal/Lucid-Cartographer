namespace LucidCartographer.Services;

/// <summary>
/// Centralized UI string constants for future i18n support.
/// Replace with resource files (.resx) or IStringLocalizer when localizing.
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
    // [TRIP-GATE-01] Announced when Trip View turns itself off because the
    // collection dropped below the two-placeable-stop minimum (e.g. a stop was
    // removed or lost its coordinates). Honest, factual — no hype, never silent.
    public const string TripViewAutoDisabledAnnouncement =
        "Trip View off — fewer than two placeable stops remain.";
    // {0} = stop number; used as the order badge's screen-reader label.
    public const string StopOrderBadgeAria = "Stop {0}";

    // Trip View — stop-list panel (Story 1.3). The dwell + timeline values are
    // inert placeholders here ("—"); the real values arrive in Epic 2.
    public const string TripStopList = "Trip stops";
    public const string TripStopListAria = "Trip stop list, in travel order";
    // {0} = stop count; announced via the panel's aria-live region.
    public const string TripStopCountAria = "{0} stop trip";
    public const string TripStopListEmpty = "No placeable stops yet.";
    // {0} = this stop's number, {1} = total stops. Row-level screen-reader label.
    public const string TripStopBadgeAria = "Stop {0} of {1}";
    // TRIP-DWELL-01 (Story 2.5): the dwell-minutes input on each stop row. {0} = the
    // stop's POI name, giving the input a per-stop accessible name. The placeholder
    // hints the unit on an empty field (the old "—" placeholder string is superseded).
    public const string TripDwellPlaceholder = "min";
    public const string TripDwellAria = "Dwell time in minutes at {0}";
    public const string TripTimelinePlaceholder = "—";
    public const string TripTimelineAria = "Arrival time (computed in a later step)";

    // Trip View — list ↔ map selection sync (Story 1.4). {0} = stop number,
    // {1} = POI name; announced via the panel's aria-live region on selection.
    public const string TripStopSelectedAnnouncement = "Selected stop {0}: {1}";
    // Selectable stop-row accessible name. {0} = stop number, {1} = total stops,
    // {2} = POI name.
    public const string TripStopRowAria = "Stop {0} of {1}: {2}";

    // Trip View — stop reorder (Story 1.5). {0} = POI name. The move-control
    // aria-labels and the aria-live announcement after every successful move.
    public const string TripMoveStopUp = "Move {0} up";
    public const string TripMoveStopDown = "Move {0} down";
    // {0} = POI name, {1} = new stop number, {2} = total stops.
    public const string TripStopMovedAnnouncement = "{0} moved to stop {1} of {2}";
    // Drag-handle accessible name. {0} = POI name.
    public const string TripDragHandle = "Drag to reorder {0}";

    // Trip View — Start/Finish designation (Story 1.7, [TRIP-STARTFINISH-05]).
    // Set/unset control labels ({0} = POI name), the distinct badge/marker
    // accessible names, and the aria-live announcements for designation and
    // roundtrip ↔ open-path shape transitions.
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
    // {0} = POI name. Announced after a Start designation / when it is cleared.
    public const string TripStartSetAnnouncement = "{0} set as start — stop 1";
    public const string TripStartClearedAnnouncement = "Start cleared";
    // Shape announcements: clearing the Finish restores the Roundtrip; a
    // distinct Finish ({0} = POI name) opens the path.
    public const string TripRoundtripAnnounce = "Roundtrip — returns to start";
    public const string TripOpenPathAnnounce = "Open path — ends at {0}";

    // Trip View — unplaceable stops (Story 1.6, [TRIP-PLACE-05]). Honest,
    // factual, provenance-aware copy (UX-DR11): the POI is kept in the
    // collection, excluded from the route — never silently dropped (UX-DR10).
    public const string TripStopNotPlaceable = "Not placeable";
    public const string TripStopNotPlaceableDetail = "Not placeable — no coordinates. Kept in the collection, excluded from the route.";
    public const string TripStopNotPlaceableAria = "Not placeable: this stop has no coordinates and is excluded from the route, but kept in the collection.";

    // Trip View — per-leg travel time + Fidelity badge (Story 2.1,
    // [TRIP-TRAVELTIME-01]). Times/distances are formatted at the UI edge only
    // (canonical seconds/meters in the VM). "—" marks an as-yet-uncomputed leg
    // or a partial total (no false precision).
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
    // Announced via aria-live while one or more legs are still computing.
    public const string TripLegComputingAnnouncement = "Computing travel times…";
    public const string TripLegComputingAria = "Travel time computing";
    // Trip total travel time. {0} = formatted total; the label introduces the value.
    public const string TripTotalTravelTimeLabel = "Total travel time";
    public const string TripTotalTravelTimeAria = "Total travel time {0}";
    // TRIP-RECOMPUTE-01 (Story 2.4, AC4/UX-DR9): the explicit "Recompute travel
    // times" control in the trip header area (both surfaces). Visible label + the
    // accessible name; on-demand only — invalidates the eligible cached legs and
    // re-requests them (never the user's Manual entries, never a Measured row).
    public const string TripRecomputeLabel = "Recompute travel times";
    public const string TripRecomputeAria = "Recompute travel times";
    // UI-edge duration/distance format patterns (TravelTimeFormatting). Kept here
    // so the unit words ("h"/"m"/"km") and the sub-minute/zero tokens are localizable
    // rather than hardcoded at the conversion site. {0}/{1} = numeric parts.
    public const string TripDurationHoursMinutes = "{0}h {1}m";
    public const string TripDurationMinutes = "{0}m";
    public const string TripDurationSubMinute = "<1m";
    public const string TripDurationZero = "0m";
    public const string TripDistanceKilometers = "{0:0.#} km";
    public const string TripDistanceMeters = "{0:0} m";
    // Accessible name for the leg-time slot of a stop that has NO departing leg
    // (the last stop of an open path, or an unplaceable row). Distinct from the
    // "computing" state — there is simply no hop to time here.
    public const string TripLegNoTravelTimeAria = "No travel time";

    // TRIP-DEGRADE-01 (Story 2.3, AC2 / UX-DR10/UX-DR11): the honest approximate
    // note shown in the trip header/panel area (an aria-live status region, warn/
    // muted tone — never error-red) when any leg fell back to a straight-line
    // estimate because the routing engine was unreachable. A normal Mock-Estimated
    // trip (the shipping default) never shows this.
    public const string TripApproximateEstimatesNote =
        "Couldn't reach the routing engine — showing straight-line estimates.";

    // Trip View — travel-mode selector (Story 2.2, TRIP-TRAVELMODE-01). Segment
    // labels + the radiogroup's accessible name. The active segment is styled
    // primary; the choice is per-trip (persisted to PoiCollection.TravelMode).
    public const string TripTravelModeSelectorAria = "Travel mode";
    public const string TripTravelModeAnyAir = "Any/Air";
    public const string TripTravelModeDrive = "Drive";
    public const string TripTravelModeWalk = "Walk";
    public const string TripTravelModeCycle = "Cycle";

    // Trip View — manual Any/Air leg time (Story 2.2, TRIP-MANUAL-01). The numeric
    // minutes input shown only on Any/Air legs; entering a value carries
    // Fidelity.Manual and overrides the placeholder. {0} = POI name of the leg's
    // origin stop, giving the input a per-leg accessible name.
    public const string TripManualMinutesPlaceholder = "min";
    public const string TripManualMinutesAria = "Manual travel time in minutes for the leg departing {0}";

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
