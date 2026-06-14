using Bunit;
using BunitTestContext = Bunit.TestContext;
using FluentAssertions;
using LucidCartographer.Components.Pages;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace LucidCartographer.Tests.Components;

public class DataSourcesPageTests : BunitTestContext
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public DataSourcesPageTests()
    {
        _factory = TestDbHelper.CreateFactory();
        Services.AddSingleton(_factory);
        Services.AddSingleton<SqliteWriteLock>();
        Services.AddScoped<LucidCartographer.Services.Trip.IRouteSegmentInvalidationService, LucidCartographer.Services.Trip.RouteSegmentInvalidationService>();
        Services.AddScoped<IPoiService, PoiService>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.AddScoped<IFileImporter, GpxImporter>();
        Services.AddScoped<IFileImporter, KmlImporter>();
        Services.AddScoped<IFileImporter, GeoJsonImporter>();
        Services.AddScoped<IFileImporter, CsvImporter>();
        Services.AddScoped<IImportOrchestrator, ImportOrchestrator>();
        Services.AddScoped<IGoogleMapsListScraper>(_ => Mock.Of<IGoogleMapsListScraper>());
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
        Services.AddSingleton(httpClientFactory.Object);
        Services.AddSingleton<Services.Enrichment.EnrichmentTrigger>();
        // Background-import wiring: the page now enqueues via
        // IImportJobQueue and subscribes to ImportJobStatusService
        // instead of awaiting IImportOrchestrator directly.
        Services.AddSingleton<ImportJobStatusService>();
        Services.AddSingleton<IImportJobQueue>(_ => Mock.Of<IImportJobQueue>());

        // Background-export wiring: the page enqueues via IExportJobQueue and
        // subscribes to ExportJobStatusService for progress.
        Services.AddSingleton<Services.Export.ExportJobStatusService>();
        Services.AddSingleton<Services.Export.IExportJobQueue>(_ => Mock.Of<Services.Export.IExportJobQueue>());

        // Page-scoped ViewModel — page now @injects this instead of
        // individual services (Stage 2 ViewModel discipline).
        Services.AddTransient<DataSourcesPageViewModel>();

        // The page hosts a ViewportObserver that resolves ViewportService and
        // calls a JS helper on first render; loose JSInterop returns defaults.
        // ViewportService now reads the lucid_viewport cookie via
        // IHttpContextAccessor on construction (HttpContext is null in bUnit,
        // so it stays Initialized=false until the JS interop call).
        Services.AddHttpContextAccessor();
        Services.AddScoped<ViewportService>();
        JSInterop.Mode = Bunit.JSRuntimeMode.Loose;
    }

    // ── Initial Render ──────────────────────────────────────────────────

    [Fact]
    public void Page_Renders_With_DataAndImports_Heading()
    {
        var cut = RenderComponent<DataSourcesPage>();

        cut.Find("h2").TextContent.Should().Contain("Data & Imports");
    }

    [Fact]
    public void Page_Renders_ManagedSources_With_Zero_Datasets()
    {
        var cut = RenderComponent<DataSourcesPage>();

        cut.Markup.Should().Contain("0 dataset(s)");
    }

    [Fact]
    public void Page_Shows_Empty_State_When_No_Datasets()
    {
        var cut = RenderComponent<DataSourcesPage>();

        cut.Markup.Should().Contain("No datasets imported yet");
    }

    // ── Card Interaction ────────────────────────────────────────────────

    [Fact]
    public void Upload_Panel_Has_CollectionName_Input_And_ColorPicker()
    {
        var cut = RenderComponent<DataSourcesPage>();

        var fileCard = cut.FindAll(".cursor-pointer")[0];
        fileCard.Click();

        cut.Find("input[placeholder*='Poland Top Places']").Should().NotBeNull();
        cut.FindAll("label").Should().Contain(l => l.TextContent.Contains("Color"));
    }

    [Fact]
    public void Clicking_Close_Button_Hides_Upload_Panel()
    {
        var cut = RenderComponent<DataSourcesPage>();

        // Open panel
        var fileCard = cut.FindAll(".cursor-pointer")[0];
        fileCard.Click();
        cut.FindAll("h3.font-bold.font-headline.text-lg").Should().NotBeEmpty();

        // Close panel — find the close button inside the upload panel header
        var uploadPanelHeader = cut.Find(".flex.items-center.justify-between.mb-4");
        var closeBtn = uploadPanelHeader.QuerySelector("button");
        closeBtn!.Click();

        // Upload panel should be gone (no panel title with text-lg inside the upload area)
        // The only text-lg h3 remaining is "Managed Sources" which is always present
        cut.FindAll("h3.font-bold.font-headline.text-lg").Should().HaveCount(1);
        cut.FindAll("h3.font-bold.font-headline.text-lg")[0].TextContent.Should().Contain("Managed Sources");
    }

    // ── Managed Sources Table (with seeded data) ────────────────────────

    private void SeedOneCollection()
    {
        using var db = _factory.CreateDbContext();
        var col = new PoiCollection
        {
            Name = "Test Set",
            Color = "#ff0000",
            PoiCount = 5,
            SourceType = "gpx_import",
            CreatedDate = new DateTime(2025, 3, 15, 0, 0, 0, DateTimeKind.Utc)
        };
        db.PoiCollections.Add(col);
        // Add actual POIs so computed PoiCount matches
        for (var i = 1; i <= 5; i++)
        {
            var poi = new Poi { Name = $"Poi{i}", Latitude = 50.0 + i, Longitude = 20.0 + i, AddedDate = DateTime.UtcNow };
            db.Pois.Add(poi);
            db.SaveChanges(); // Flush to get IDs
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = poi.Id, PoiCollectionId = col.Id });
        }
        db.SaveChanges();
    }

    [Fact]
    public void Table_Shows_Collection_Name()
    {
        SeedOneCollection();
        var cut = RenderComponent<DataSourcesPage>();

        cut.Find("table").TextContent.Should().Contain("Test Set");
    }

    [Fact]
    public void Table_Shows_Point_Count()
    {
        SeedOneCollection();
        var cut = RenderComponent<DataSourcesPage>();

        // PoiCount = 5, formatted with "N0" => "5"
        var cells = cut.FindAll("table td");
        cells.Should().Contain(td => td.TextContent.Trim() == "5");
    }

    [Fact]
    public void Table_Shows_Source_Type()
    {
        SeedOneCollection();
        var cut = RenderComponent<DataSourcesPage>();

        cut.Find("table").TextContent.Should().Contain("gpx_import");
    }

    [Fact]
    public void Table_Shows_Date()
    {
        SeedOneCollection();
        var cut = RenderComponent<DataSourcesPage>();

        cut.Find("table").TextContent.Should().Contain("Mar 15, 2025");
    }

    [Fact]
    public void Table_Has_Delete_Button_For_Each_Row()
    {
        SeedOneCollection();
        var cut = RenderComponent<DataSourcesPage>();

        var deleteButtons = cut.FindAll("table tbody button")
            .Where(btn => btn.GetAttribute("aria-label")?.StartsWith("Delete ") == true)
            .ToList();
        deleteButtons.Should().HaveCount(1);
        deleteButtons[0].InnerHtml.Should().Contain("delete");
    }

    [Fact]
    public void Table_Has_GoogleSavedList_Export_Button_For_Each_Row()
    {
        SeedOneCollection();
        var cut = RenderComponent<DataSourcesPage>();

        var btn = cut.FindAll("table tbody button")
            .Single(b => b.GetAttribute("aria-label") == "Export Test Set to a Google Maps Saved List");
        btn.InnerHtml.Should().Contain("bookmark_add");
    }

    [Fact]
    public void Clicking_Collection_Color_Opens_Color_Picker_Modal()
    {
        SeedOneCollection();
        var cut = RenderComponent<DataSourcesPage>();

        cut.Find("button[aria-label='Change color for Test Set']").Click();

        cut.Find("div[role='dialog'] h4").TextContent.Should().Contain("Change color: Test Set");
        cut.Find("input[type='color']").GetAttribute("value").Should().Be("#ff0000");
    }

    [Fact]
    public void Saving_Collection_Color_Updates_Database()
    {
        SeedOneCollection();
        var cut = RenderComponent<DataSourcesPage>();

        cut.Find("button[aria-label='Change color for Test Set']").Click();
        cut.Find("input[type='color']").Change("#006e2c");
        cut.Find("div[role='dialog'] button.bg-primary").Click();

        cut.WaitForAssertion(() =>
        {
            using var db = _factory.CreateDbContext();
            db.PoiCollections.Single(c => c.Name == "Test Set").Color.Should().Be("#006e2c");
        });
    }

    [Fact]
    public void Shows_One_Dataset_Count()
    {
        SeedOneCollection();
        var cut = RenderComponent<DataSourcesPage>();

        cut.Markup.Should().Contain("1 dataset(s)");
    }

    // ── Color Picker ────────────────────────────────────────────────────

    [Fact]
    public void Clicking_Color_Changes_Selected_State()
    {
        var cut = RenderComponent<DataSourcesPage>();

        // Open upload panel
        var fileCard = cut.FindAll(".cursor-pointer")[0];
        fileCard.Click();

        var colorButtons = cut.FindAll("button.rounded-full");

        // First button (#005bbf) is selected by default - should have ring-2
        colorButtons[0].ClassList.Should().Contain("ring-2");

        // Click third color (#b81d17)
        colorButtons[2].Click();

        // Re-query after re-render
        var updatedButtons = cut.FindAll("button.rounded-full");
        updatedButtons[2].ClassList.Should().Contain("ring-2");
        updatedButtons[0].ClassList.Should().NotContain("ring-2");
    }

    // ── Import Status Flow (BehaviorSubject replay on subscribe) ─────────

    [Fact]
    public void Renders_Upload_Panel_Initially_Without_Import_Status()
    {
        var cut = RenderComponent<DataSourcesPage>();

        // Open upload panel
        var fileCard = cut.FindAll(".cursor-pointer")[0];
        fileCard.Click();

        // Panel is open; no import status should be shown yet
        // (StatusMessage parameter passes _queuedMessage, which is null initially)
        var fileInput = cut.Find("input[type='file']");
        fileInput.Should().NotBeNull();
    }
}