using System.Reflection;
using Bunit;
using BunitTestContext = Bunit.TestContext;
using FluentAssertions;
using LucidCartographer.Components.Pages;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Export;
using LucidCartographer.Services.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Moq;

namespace LucidCartographer.Tests.Components
{
    public class OperationsPageTests : BunitTestContext
    {
        private readonly IDbContextFactory<AppDbContext> _factory;

        // Under bUnit, the <Virtualize> component inside OperationsPage renders zero
        // <tr> rows because there is no real viewport. Tests that need to inspect
        // result contents read the underlying state via reflection instead.
        private static IList<Poi> GetResultPois(IRenderedComponent<OperationsPage> cut)
        {
            var f = typeof(OperationsPage).GetField("_resultPois",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return ((IEnumerable<Poi>)f!.GetValue(cut.Instance)!).ToList();
        }

        private static HashSet<int> GetDiscardedIds(IRenderedComponent<OperationsPage> cut)
        {
            var f = typeof(OperationsPage).GetField("_discardedIds",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return (HashSet<int>)f!.GetValue(cut.Instance)!;
        }

        private static void InvokeDiscard(IRenderedComponent<OperationsPage> cut, int poiId)
        {
            var m = typeof(OperationsPage).GetMethod("DiscardPoi",
                BindingFlags.Instance | BindingFlags.NonPublic);
            m!.Invoke(cut.Instance, new object[] { poiId });
        }

        private static void InvokeRestore(IRenderedComponent<OperationsPage> cut, int poiId)
        {
            var m = typeof(OperationsPage).GetMethod("RestorePoi",
                BindingFlags.Instance | BindingFlags.NonPublic);
            m!.Invoke(cut.Instance, new object[] { poiId });
        }

        public OperationsPageTests()
        {
            _factory = TestDbHelper.CreateFactory();
            Services.AddSingleton(_factory);
            Services.AddScoped<IPoiService, PoiService>();
            Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            Services.AddScoped<IPoiMatcher, PoiMatcher>();
            Services.AddScoped<ISetOperationService, SetOperationService>();
            Services.AddSingleton<KmlExporter>();
            Services.AddSingleton(new Mock<IJSRuntime>().Object);
        }

        private void SeedData()
        {
            using var db = _factory.CreateDbContext();

            var colA = new PoiCollection { Name = "Visited", Color = "#006e2c", PoiCount = 3 };
            var colB = new PoiCollection { Name = "Want to go", Color = "#005bbf", PoiCount = 2 };
            db.PoiCollections.AddRange(colA, colB);
            db.SaveChanges();

            var poi1 = new Poi { Name = "Wawel Castle", Latitude = 50.054, Longitude = 19.935, GoogleMapsUrl = "https://maps.google.com/wawel" };
            var poi2 = new Poi { Name = "Wieliczka Mine", Latitude = 49.983, Longitude = 20.055 };
            var poi3 = new Poi { Name = "Malbork Castle", Latitude = 54.040, Longitude = 19.028 };
            var poi4 = new Poi { Name = "Wawel Castle", Latitude = 50.054, Longitude = 19.935, GoogleMapsUrl = "https://maps.google.com/wawel" }; // same as poi1
            var poi5 = new Poi { Name = "Gdansk Old Town", Latitude = 54.352, Longitude = 18.646 };
            db.Pois.AddRange(poi1, poi2, poi3, poi4, poi5);
            db.SaveChanges();

            db.PoiCollectionItems.AddRange(
                new PoiCollectionItem { PoiId = poi1.Id, PoiCollectionId = colA.Id },
                new PoiCollectionItem { PoiId = poi2.Id, PoiCollectionId = colA.Id },
                new PoiCollectionItem { PoiId = poi3.Id, PoiCollectionId = colA.Id },
                new PoiCollectionItem { PoiId = poi4.Id, PoiCollectionId = colB.Id },
                new PoiCollectionItem { PoiId = poi5.Id, PoiCollectionId = colB.Id }
            );
            db.SaveChanges();
        }

        /// <summary>
        /// Selects both collections in the dropdowns and clicks the specified operation button.
        /// Re-finds DOM elements after each change to avoid stale element references.
        /// </summary>
        private async Task SelectCollectionsAndRunOperation(IRenderedComponent<OperationsPage> cut, string colAId, string colBId, string operationLabel)
        {
            await cut.InvokeAsync(() => cut.FindAll("select")[0].Change(colAId));
            await cut.InvokeAsync(() => cut.FindAll("select")[1].Change(colBId));
            var btn = cut.FindAll("button").First(b => b.TextContent.Contains(operationLabel));
            await cut.InvokeAsync(() => btn.Click());
            cut.WaitForState(() => cut.FindAll("table").Count > 0, TimeSpan.FromSeconds(3));
        }

        private async Task SelectBothCollections(IRenderedComponent<OperationsPage> cut, string colAId, string colBId)
        {
            await cut.InvokeAsync(() => cut.FindAll("select")[0].Change(colAId));
            await cut.InvokeAsync(() => cut.FindAll("select")[1].Change(colBId));
        }

        // ── Initial Render (no data) ──

        [Fact]
        public void Renders_SourceSelectionHeading()
        {
            var cut = RenderComponent<OperationsPage>();

            cut.Markup.Should().Contain("Source Selection");
        }

        [Fact]
        public void Renders_SourceDatasetLabels()
        {
            var cut = RenderComponent<OperationsPage>();

            cut.Markup.Should().Contain("Source Dataset A");
            cut.Markup.Should().Contain("Source Dataset B");
        }

        [Fact]
        public void Renders_SpatialToleranceSlider_WithDefaultValue()
        {
            var cut = RenderComponent<OperationsPage>();

            var slider = cut.Find("input[type='range']");
            slider.Should().NotBeNull();
            cut.Markup.Should().Contain("Spatial Tolerance:");
            cut.Markup.Should().Contain("100m");
        }

        [Fact]
        public void Renders_EmptyStateMessage_WhenNoOperationRun()
        {
            var cut = RenderComponent<OperationsPage>();

            cut.Markup.Should().Contain("Select datasets and run an operation");
        }

        // ── With Seeded Data ──

        [Fact]
        public void Dropdowns_ContainCollectionOptions_WhenDataSeeded()
        {
            SeedData();
            var cut = RenderComponent<OperationsPage>();

            cut.Markup.Should().Contain("Visited");
            cut.Markup.Should().Contain("Want to go");
        }

        [Fact]
        public void Dropdowns_ShowPointCounts_InOptionText()
        {
            SeedData();
            var cut = RenderComponent<OperationsPage>();

            cut.Markup.Should().Contain("3 pts");
            cut.Markup.Should().Contain("2 pts");
        }

        // ── Operation Buttons State ──

        [Fact]
        public void BinaryOperationButtons_AreDisabled_WhenNoCollectionsSelected()
        {
            SeedData();
            var cut = RenderComponent<OperationsPage>();

            var buttons = cut.FindAll("button");
            var subtractBtn = buttons.First(b => b.TextContent.Contains("A - B"));
            var intersectBtn = buttons.First(b => b.TextContent.Contains("A n B"));
            var unionBtn = buttons.First(b => b.TextContent.Contains("A u B"));

            subtractBtn.HasAttribute("disabled").Should().BeTrue();
            intersectBtn.HasAttribute("disabled").Should().BeTrue();
            unionBtn.HasAttribute("disabled").Should().BeTrue();
        }

        [Fact]
        public void DedupButton_IsDisabled_WhenCollectionANotSelected()
        {
            SeedData();
            var cut = RenderComponent<OperationsPage>();

            var buttons = cut.FindAll("button");
            var dedupBtn = buttons.First(b => b.TextContent.Contains("Dedup"));

            dedupBtn.HasAttribute("disabled").Should().BeTrue();
        }

        [Fact]
        public async Task BinaryButtons_BecomeEnabled_AfterSelectingBothCollections()
        {
            SeedData();
            var cut = RenderComponent<OperationsPage>();

            using var db = _factory.CreateDbContext();
            var colA = db.PoiCollections.First(c => c.Name == "Visited");
            var colB = db.PoiCollections.First(c => c.Name == "Want to go");

            await SelectBothCollections(cut, colA.Id.ToString(), colB.Id.ToString());

            var buttons = cut.FindAll("button");
            var subtractBtn = buttons.First(b => b.TextContent.Contains("A - B"));
            var intersectBtn = buttons.First(b => b.TextContent.Contains("A n B"));
            var unionBtn = buttons.First(b => b.TextContent.Contains("A u B"));

            subtractBtn.HasAttribute("disabled").Should().BeFalse();
            intersectBtn.HasAttribute("disabled").Should().BeFalse();
            unionBtn.HasAttribute("disabled").Should().BeFalse();
        }

        // ── Running Operations ──

        [Fact]
        public async Task Subtract_ShowsResultTable_WithCorrectCount()
        {
            SeedData();
            var cut = RenderComponent<OperationsPage>();

            using var db = _factory.CreateDbContext();
            var colA = db.PoiCollections.First(c => c.Name == "Visited");
            var colB = db.PoiCollections.First(c => c.Name == "Want to go");

            await SelectCollectionsAndRunOperation(cut, colA.Id.ToString(), colB.Id.ToString(), "A - B");

            var table = cut.Find("table");
            table.Should().NotBeNull();

            // A - B: Wawel Castle matches between A and B (same URL), so result should be Wieliczka Mine + Malbork Castle = 2
            // NOTE: <Virtualize> renders 0 rows under bUnit, so assert against the underlying state.
            GetResultPois(cut).Should().HaveCount(2);
        }

        [Fact]
        public async Task Subtract_ResultTable_ShowsPoiNames()
        {
            SeedData();
            var cut = RenderComponent<OperationsPage>();

            using var db = _factory.CreateDbContext();
            var colA = db.PoiCollections.First(c => c.Name == "Visited");
            var colB = db.PoiCollections.First(c => c.Name == "Want to go");

            await SelectCollectionsAndRunOperation(cut, colA.Id.ToString(), colB.Id.ToString(), "A - B");

            var names = GetResultPois(cut).Select(p => p.Name).ToList();
            names.Should().Contain("Wieliczka Mine");
            names.Should().Contain("Malbork Castle");
        }

        [Fact]
        public async Task Subtract_ResultHeader_ShowsOperationLabel()
        {
            SeedData();
            var cut = RenderComponent<OperationsPage>();

            using var db = _factory.CreateDbContext();
            var colA = db.PoiCollections.First(c => c.Name == "Visited");
            var colB = db.PoiCollections.First(c => c.Name == "Want to go");

            await SelectCollectionsAndRunOperation(cut, colA.Id.ToString(), colB.Id.ToString(), "A - B");

            cut.Markup.Should().Contain("Result Preview: A - B");
        }

        [Fact]
        public async Task Intersect_ShowsCommonPois()
        {
            SeedData();
            var cut = RenderComponent<OperationsPage>();

            using var db = _factory.CreateDbContext();
            var colA = db.PoiCollections.First(c => c.Name == "Visited");
            var colB = db.PoiCollections.First(c => c.Name == "Want to go");

            await SelectCollectionsAndRunOperation(cut, colA.Id.ToString(), colB.Id.ToString(), "A n B");

            // Intersection: Wawel Castle is common (URL match)
            var result = GetResultPois(cut);
            result.Should().HaveCount(1);
            result.Select(p => p.Name).Should().Contain("Wawel Castle");
        }

        [Fact]
        public async Task Union_ShowsMergedPois()
        {
            SeedData();
            var cut = RenderComponent<OperationsPage>();

            using var db = _factory.CreateDbContext();
            var colA = db.PoiCollections.First(c => c.Name == "Visited");
            var colB = db.PoiCollections.First(c => c.Name == "Want to go");

            await SelectCollectionsAndRunOperation(cut, colA.Id.ToString(), colB.Id.ToString(), "A u B");

            // Union: A has 3 POIs, B has Wawel (duplicate) + Gdansk. Result = 3 + 1 = 4
            var result = GetResultPois(cut);
            result.Should().HaveCount(4);
            var names = result.Select(p => p.Name).ToList();
            names.Should().Contain("Wawel Castle");
            names.Should().Contain("Wieliczka Mine");
            names.Should().Contain("Malbork Castle");
            names.Should().Contain("Gdansk Old Town");
        }

        // ── Result Actions ──

        [Fact]
        public async Task RestoreButton_AppearsAfterDiscard_AndRestoresRow()
        {
            SeedData();
            var cut = RenderComponent<OperationsPage>();

            using var db = _factory.CreateDbContext();
            var colA = db.PoiCollections.First(c => c.Name == "Visited");
            var colB = db.PoiCollections.First(c => c.Name == "Want to go");

            await SelectCollectionsAndRunOperation(cut, colA.Id.ToString(), colB.Id.ToString(), "A - B");

            // Rows live inside <Virtualize> and don't render under bUnit; drive Discard/Restore
            // via the component's internal methods and assert against _discardedIds.
            var firstPoi = GetResultPois(cut).First();

            await cut.InvokeAsync(() => InvokeDiscard(cut, firstPoi.Id));
            GetDiscardedIds(cut).Should().Contain(firstPoi.Id);

            await cut.InvokeAsync(() => InvokeRestore(cut, firstPoi.Id));
            GetDiscardedIds(cut).Should().NotContain(firstPoi.Id);
        }

        [Fact]
        public async Task DiscardedCount_ShownInFooter()
        {
            SeedData();
            var cut = RenderComponent<OperationsPage>();

            using var db = _factory.CreateDbContext();
            var colA = db.PoiCollections.First(c => c.Name == "Visited");
            var colB = db.PoiCollections.First(c => c.Name == "Want to go");

            await SelectCollectionsAndRunOperation(cut, colA.Id.ToString(), colB.Id.ToString(), "A - B");

            // Virtualize hides rows under bUnit; invoke Discard via reflection instead.
            var firstPoi = GetResultPois(cut).First();
            await cut.InvokeAsync(() => InvokeDiscard(cut, firstPoi.Id));
            cut.Render();

            cut.Markup.Should().Contain("1 discarded");
        }

        [Fact]
        public async Task ExportResultButton_IsPresent_WhenResultsExist()
        {
            SeedData();
            var cut = RenderComponent<OperationsPage>();

            using var db = _factory.CreateDbContext();
            var colA = db.PoiCollections.First(c => c.Name == "Visited");
            var colB = db.PoiCollections.First(c => c.Name == "Want to go");

            await SelectCollectionsAndRunOperation(cut, colA.Id.ToString(), colB.Id.ToString(), "A - B");

            var exportBtn = cut.FindAll("button").FirstOrDefault(b => b.TextContent.Contains("Export Result"));
            exportBtn.Should().NotBeNull();
        }

        // ── Tolerance Slider ──

        [Fact]
        public void ChangingToleranceValue_UpdatesDisplayedText()
        {
            var cut = RenderComponent<OperationsPage>();

            var slider = cut.Find("input[type='range']");
            slider.Input("250");

            cut.Markup.Should().Contain("250m");
        }
    }
}
