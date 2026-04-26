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
    public const string Cancel = "Cancel";
    public const string Save = "Save";
    public const string Yes = "Yes";
    public const string No = "No";
    public const string Logout = "Logout";
    public const string SearchPois = "Search POIs...";
    public const string Settings = "Settings";

    // Navigation
    public const string NavMap = "Map";
    public const string NavDataSources = "Data Sources";
    public const string NavOperations = "Operations";

    // Map Page
    public const string MapPageTitle = "Map - Lucid Cartographer";
    public const string FitAll = "Fit All";
    public const string FitMapToVisible = "Fit map to all visible collections";
    public const string FilteredResults = "Filtered Results";
    public const string NoPoiToDisplay = "No POIs to display. Toggle collection visibility or import data.";
    public const string LocationDetails = "Location Details";
    public const string CloseDetailPane = "Close detail pane";
    public const string OpenInGoogleMaps = "Open in Google Maps";
    public const string EnrichDetails = "Enrich Details";
    public const string EnrichDetailsAria = "Re-fetch address, website, phone, and image for this location";
    public const string EnrichFallbackTitle = "Couldn't find details for {0}";
    public const string EnrichFallbackBody = "Open Google Maps, locate the correct place, then paste its URL below to enrich this POI.";
    public const string EnrichFallbackSearchLink = "Search on Google Maps";
    public const string EnrichFallbackUrlLabel = "Google Maps place URL";
    public const string EnrichFallbackInvalidUrl = "Please paste a Google Maps URL (google.com/maps/... or maps.app.goo.gl).";
    public const string Collections = "Collections";
    public const string NoCollectionsYet = "No collections yet.";
    public const string ImportDataHint = "Import data via Data Sources tab.";

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
