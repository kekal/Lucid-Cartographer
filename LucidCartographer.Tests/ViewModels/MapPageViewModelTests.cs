using FluentAssertions;
using LucidCartographer.Components.Pages;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Enrichment;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace LucidCartographer.Tests.ViewModels;

/// <summary>
/// Plain xUnit tests for the Map page VM. Excludes paths that depend on the
/// LeafletMap JS interop instance (those are covered by Playwright integration
/// tests). Focuses on collection / selection / viewport-filter state.
/// </summary>
public class MapPageViewModelTests
{
    private readonly Mock<IPoiService> _poi = new();
    private readonly EnrichmentProgressService _progress = new();
    private readonly EnrichmentTrigger _trigger = new();
    private readonly TestNavigationManager _navigation = new();

    private MapPageViewModel CreateVm()
    {
        return new MapPageViewModel(
            _poi.Object,
            _navigation,
            _progress,
            _trigger,
            NullLogger<MapPageViewModel>.Instance);
    }

    private static PoiCollection MakeCollection(int id, string name, bool isVisible = true)
        => new() { Id = id, Name = name, Color = "#005bbf", IsVisible = isVisible };

    private static Poi MakePoi(int id, double? lat = null, double? lon = null)
        => new() { Id = id, Name = $"P{id}", Latitude = lat, Longitude = lon };

    [Fact]
    public async Task Initialize_LoadsCollections_PopulatesStates_ClearsLoading()
    {
        List<PoiCollection> collections =
        [
            MakeCollection(1, "A", isVisible: true),
            MakeCollection(2, "B", isVisible: false)
        ];
        _poi.Setup(p => p.GetCollectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(collections);

        var vm = CreateVm();
        await vm.InitializeAsync();

        vm.IsLoading.Should().BeFalse();
        vm.Collections.Should().HaveCount(2);
        vm.CollectionStates.Should().HaveCount(2);
        vm.CollectionStates[0].IsVisible.Should().BeTrue();
        vm.CollectionStates[1].IsVisible.Should().BeFalse();
    }

    [Fact]
    public void OnSplitterResized_UpdatesTableHeight_AndNotifies()
    {
        var vm = CreateVm();
        var fired = 0;
        vm.StateChanged += () => fired++;

        var task = vm.OnSplitterResizedJs(420);

        task.IsCompleted.Should().BeTrue();
        vm.TableHeight.Should().Be(420);
        fired.Should().Be(1);
    }

    [Fact]
    public void HandleBoundsChanged_AppliesViewportFilter_NotifiesOnce()
    {
        List<Poi> pois =
        [
            MakePoi(1, lat: 50.0, lon: 19.0),
            MakePoi(2, lat: 60.0, lon: 19.0) // outside the test bounds
        ];
        var vm = CreateVm();
        // Reach via reflection — VisiblePois has a private setter.
        typeof(MapPageViewModel).GetProperty(nameof(MapPageViewModel.VisiblePois))!
            .SetValue(vm, (IReadOnlyList<Poi>)pois);

        var fired = 0;
        vm.StateChanged += () => fired++;

        // MapBounds(South, West, North, East) — the test box covers lat 49–51.
        vm.HandleBoundsChanged(new MapBounds(49.0, 18.0, 51.0, 20.0));

        vm.FilteredPois.Should().ContainSingle().Which.Id.Should().Be(1);
        fired.Should().Be(1);
    }

    [Fact]
    public async Task CollectionPlaceablePoiCount_CountsFullMembership_IndependentOfViewport()
    {
        _poi.Setup(p => p.GetCollectionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<PoiCollection>)[MakeCollection(1, "A", isVisible: true)]);
        var vm = CreateVm();
        await vm.InitializeAsync();

        // Full membership: 2 placeable + 1 without coordinates.
        typeof(MapPageViewModel).GetProperty(nameof(MapPageViewModel.VisiblePois))!
            .SetValue(vm, (IReadOnlyList<Poi>)[MakePoi(1, 50.0, 19.0), MakePoi(2, 51.0, 19.0), MakePoi(3)]);
        // Viewport currently excludes everything (e.g. panned away).
        typeof(MapPageViewModel).GetProperty(nameof(MapPageViewModel.FilteredPois))!
            .SetValue(vm, (IReadOnlyList<Poi>)[]);

        vm.SingleVisibleCollectionId.Should().Be(1);
        vm.PlaceablePoiCount.Should().Be(0, "the viewport-filtered set is empty");
        vm.CollectionPlaceablePoiCount.Should()
            .Be(2, "the gate counts the collection's full placeable membership, not the viewport subset");
    }

    [Fact]
    public async Task CollectionPlaceablePoiCount_IsZero_WhenNotASingleVisibleCollection()
    {
        _poi.Setup(p => p.GetCollectionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<PoiCollection>)[MakeCollection(1, "A", isVisible: true), MakeCollection(2, "B", isVisible: true)]);
        var vm = CreateVm();
        await vm.InitializeAsync();

        typeof(MapPageViewModel).GetProperty(nameof(MapPageViewModel.VisiblePois))!
            .SetValue(vm, (IReadOnlyList<Poi>)[MakePoi(1, 50.0, 19.0)]);

        vm.SingleVisibleCollectionId.Should().BeNull();
        vm.CollectionPlaceablePoiCount.Should().Be(0, "the gate stays closed unless exactly one collection is visible");
    }

    [Fact]
    public async Task CollectionPlaceablePoiCount_IsZero_DuringActiveSearch()
    {
        _poi.Setup(p => p.GetCollectionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<PoiCollection>)[MakeCollection(1, "A", isVisible: true)]);
        var vm = CreateVm();
        await vm.InitializeAsync();
        typeof(MapPageViewModel).GetProperty(nameof(MapPageViewModel.PreviousSearchQuery))!
            .SetValue(vm, "wawel");
        typeof(MapPageViewModel).GetProperty(nameof(MapPageViewModel.VisiblePois))!
            .SetValue(vm, (IReadOnlyList<Poi>)[MakePoi(1, 50.0, 19.0)]);

        vm.SingleVisibleCollectionId.Should().BeNull("an active search suppresses single-collection scope");
        vm.CollectionPlaceablePoiCount.Should().Be(0);
    }

    [Fact]
    public async Task ResetSearch_ClearsState_AndNavigatesHome()
    {
        var vm = CreateVm();
        typeof(MapPageViewModel).GetProperty(nameof(MapPageViewModel.PreviousSearchQuery))!
            .SetValue(vm, "wawel");
        typeof(MapPageViewModel).GetProperty(nameof(MapPageViewModel.SelectedCollectionName))!
            .SetValue(vm, "Search: wawel");

        await vm.ResetSearchAsync();

        vm.PreviousSearchQuery.Should().BeNull();
        vm.SelectedCollectionName.Should().BeNull();
        _navigation.LastNavigatedTo.Should().Be("/");
    }

    [Fact]
    public async Task OnNavigationChanged_WithSearchQuery_PopulatesSearchState()
    {
        _poi.Setup(p => p.SearchAsync("wawel", It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<Poi>)[MakePoi(1)]);

        var vm = CreateVm();
        _navigation.SetUri("http://test/?search=wawel");

        await vm.OnNavigationChangedAsync();

        vm.PreviousSearchQuery.Should().Be("wawel");
        vm.VisiblePois.Should().HaveCount(1);
        vm.PendingSearchMapUpdate.Should().BeTrue();
    }

    [Fact]
    public async Task OnNavigationChanged_SameQueryTwice_NoOp()
    {
        _poi.Setup(p => p.SearchAsync("wawel", It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<Poi>)[MakePoi(1)]);

        var vm = CreateVm();
        _navigation.SetUri("http://test/?search=wawel");
        await vm.OnNavigationChangedAsync();

        // Second call with same query should not re-search.
        await vm.OnNavigationChangedAsync();

        _poi.Verify(p => p.SearchAsync("wawel", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Test double for NavigationManager. Exposes Initialize and a way to
    /// override the Uri without going through NavigateTo, plus a recorder
    /// for outbound navigation.
    /// </summary>
    private sealed class TestNavigationManager : NavigationManager
    {
        public string? LastNavigatedTo { get; private set; }

        public TestNavigationManager() => Initialize("http://test/", "http://test/");

        public void SetUri(string uri) => Uri = uri;

        protected override void NavigateToCore(string uri, bool forceLoad) => LastNavigatedTo = uri;

        protected override void NavigateToCore(string uri, NavigationOptions options) => LastNavigatedTo = uri;
    }
}
