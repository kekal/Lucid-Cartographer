using FluentAssertions;
using LucidCartographer.Components.Pages;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Export;
using LucidCartographer.Services.Operations;
using Microsoft.JSInterop;
using Moq;

namespace LucidCartographer.Tests.ViewModels;

/// <summary>
/// Plain xUnit tests for the Operations page VM. Mocks all dependencies and
/// asserts state mutations + service calls without rendering the component.
/// </summary>
public class OperationsPageViewModelTests
{
    private readonly Mock<IPoiService> _poi = new();
    private readonly Mock<ISetOperationService> _ops = new();
    private readonly Mock<IPoiDeduplicationService> _dedup = new();
    private readonly Mock<IFileExporter> _exporter = new();
    private readonly Mock<IJSRuntime> _js = new();

    private OperationsPageViewModel CreateVm()
    {
        _exporter.SetupGet(e => e.FormatName).Returns("KML");
        return new OperationsPageViewModel(_poi.Object, _ops.Object, _dedup.Object, [_exporter.Object], _js.Object);
    }

    private static PoiCollection MakeCollection(int id, string name)
        => new() { Id = id, Name = name, Color = "#005bbf" };

    private static Poi MakePoi(int id, string name = "P", double? lat = null, double? lon = null)
        => new() { Id = id, Name = name, Latitude = lat, Longitude = lon };

    [Fact]
    public async Task Initialize_LoadsCollections_AndClearsLoadingFlag()
    {
        List<PoiCollection> collections =
        [
            MakeCollection(1, "A"),
            MakeCollection(2, "B")
        ];
        _poi.Setup(p => p.GetCollectionsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(collections);

        var vm = CreateVm();
        await vm.InitializeAsync();

        vm.IsLoading.Should().BeFalse();
        vm.Collections.Should().HaveCount(2);
    }

    [Fact]
    public async Task RunOperation_PopulatesResult_AndTogglesProcessingFlag()
    {
        List<Poi> pois = [MakePoi(1, "P1")];
        _ops.Setup(s => s.ExecuteAsync(SetOperation.Subtract, 1, 2, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationResult { Description = "ok", Pois = pois });

        var vm = CreateVm();
        vm.CollectionAId = 1;
        vm.CollectionBId = 2;

        await vm.RunOperationAsync(SetOperation.Subtract);

        vm.IsProcessing.Should().BeFalse();
        vm.ActiveOp.Should().Be(SetOperation.Subtract);
        vm.Result.Should().NotBeNull();
        vm.ResultPois.Should().HaveCount(1);
        vm.IsDedupMode.Should().BeFalse();
    }

    [Fact]
    public async Task RunOperation_Dedup_PassesNullForCollectionB()
    {
        _ops.Setup(s => s.ExecuteAsync(SetOperation.Dedup, 1, null, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationResult { Description = "deduped", Pois = [] });

        var vm = CreateVm();
        vm.CollectionAId = 1;
        vm.CollectionBId = 99; // should be ignored for Dedup

        await vm.RunOperationAsync(SetOperation.Dedup);

        vm.IsDedupMode.Should().BeTrue();
        _ops.Verify(s => s.ExecuteAsync(SetOperation.Dedup, 1, null, 100, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleBinaryOpClick_WithoutCollectionB_SetsHint_NoOpRuns()
    {
        var vm = CreateVm();
        vm.CollectionAId = 1;
        vm.CollectionBId = 0;

        await vm.HandleBinaryOpClickAsync(SetOperation.Subtract);

        vm.SelectBHint.Should().NotBeNullOrEmpty();
        _ops.Verify(s => s.ExecuteAsync(It.IsAny<SetOperation>(), It.IsAny<int>(), It.IsAny<int?>(),
            It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void DiscardPoi_AndRestorePoi_MutateDiscardedSet()
    {
        var vm = CreateVm();

        vm.DiscardPoi(42);
        vm.DiscardedIds.Should().Contain(42);

        vm.RestorePoi(42);
        vm.DiscardedIds.Should().NotContain(42);
    }

    [Fact]
    public async Task DoCommit_PassesNonDiscardedPois_AndUpdatesSuccessMessage()
    {
        List<Poi> pois =
        [
            MakePoi(1, "P1"),
            MakePoi(2, "P2")
        ];
        _ops.Setup(s => s.ExecuteAsync(It.IsAny<SetOperation>(), It.IsAny<int>(), It.IsAny<int?>(),
                It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationResult { Description = "ok", Pois = pois });
        _ops.Setup(s => s.CommitResultAsync(It.IsAny<List<Poi>>(), "MyName", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PoiCollection { Id = 99, Name = "MyName", Color = "#005bbf", PoiCount = 1 });

        var vm = CreateVm();
        vm.CollectionAId = 1; vm.CollectionBId = 2;
        await vm.RunOperationAsync(SetOperation.Subtract);

        vm.DiscardPoi(2);
        vm.CommitName = "MyName";
        await vm.DoCommitAsync();

        vm.CommitSuccess.Should().Contain("MyName");
        _ops.Verify(s => s.CommitResultAsync(
            It.Is<List<Poi>>(list => list.Count == 1 && list[0].Id == 1),
            "MyName", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GetOperationLabel_ReturnsExpectedString()
    {
        var vm = CreateVm();

        vm.GetOperationLabel().Should().BeEmpty();
    }

    [Fact]
    public async Task RunOperation_RaisesStateChanged()
    {
        _ops.Setup(s => s.ExecuteAsync(It.IsAny<SetOperation>(), It.IsAny<int>(), It.IsAny<int?>(),
                It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationResult { Description = "ok", Pois = [] });

        var vm = CreateVm();
        vm.CollectionAId = 1; vm.CollectionBId = 2;
        var fired = 0;
        vm.StateChanged += () => fired++;

        await vm.RunOperationAsync(SetOperation.Subtract);

        // Notify happens at least once (start of method) before async work.
        fired.Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task HandleDeduplicateDatabase_WithMerges_ReportsCount_AndReloads()
    {
        _poi.Setup(p => p.GetCollectionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<PoiCollection>)[MakeCollection(1, "A")]);
        _dedup.Setup(d => d.DeduplicateAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DedupResult(2, 5));

        var vm = CreateVm();
        await vm.HandleDeduplicateDatabaseAsync();

        vm.IsDeduplicatingDatabase.Should().BeFalse();
        vm.DedupDatabaseMessage.Should().Be(string.Format(UiStrings.DeduplicateDone, 5, 2));
        _dedup.Verify(d => d.DeduplicateAllAsync(It.IsAny<CancellationToken>()), Times.Once);
        _poi.Verify(p => p.GetCollectionsAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task HandleDeduplicateDatabase_NoDuplicates_ReportsClean()
    {
        _poi.Setup(p => p.GetCollectionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<PoiCollection>)[]);
        _dedup.Setup(d => d.DeduplicateAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DedupResult(0, 0));

        var vm = CreateVm();
        await vm.HandleDeduplicateDatabaseAsync();

        vm.DedupDatabaseMessage.Should().Be(UiStrings.DeduplicateNone);
    }

    [Fact]
    public async Task HandleDeduplicateDatabase_ServiceThrows_SurfacesError()
    {
        _dedup.Setup(d => d.DeduplicateAllAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var vm = CreateVm();
        await vm.HandleDeduplicateDatabaseAsync();

        vm.IsDeduplicatingDatabase.Should().BeFalse();
        vm.DedupDatabaseMessage.Should().Contain("boom");
    }

    [Fact]
    public async Task HandleDeduplicateDatabase_InvalidatesStaleDedupPreview()
    {
        // Run a within-collection Dedup first so a preview exists.
        _ops.Setup(s => s.ExecuteAsync(SetOperation.Dedup, 1, null, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationResult { Description = "deduped", Pois = [MakePoi(1, "P1")] });
        _poi.Setup(p => p.GetCollectionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<PoiCollection>)[MakeCollection(1, "A")]);
        _dedup.Setup(d => d.DeduplicateAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DedupResult(1, 1));

        var vm = CreateVm();
        vm.CollectionAId = 1;
        await vm.RunOperationAsync(SetOperation.Dedup);
        vm.DiscardPoi(1);
        vm.Result.Should().NotBeNull();
        vm.ResultPois.Should().NotBeEmpty();

        // The whole-DB pass may delete a previewed row, so the preview must be
        // cleared to stop the now-defunct rows being committed.
        await vm.HandleDeduplicateDatabaseAsync();

        vm.Result.Should().BeNull();
        vm.ResultPois.Should().BeEmpty();
        vm.DiscardedIds.Should().BeEmpty();
        vm.ActiveOp.Should().BeNull();
    }

    [Fact]
    public async Task DoCommit_WhenServiceThrows_SurfacesMessage_DoesNotPropagate()
    {
        _ops.Setup(s => s.ExecuteAsync(It.IsAny<SetOperation>(), It.IsAny<int>(), It.IsAny<int?>(),
                It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationResult { Description = "ok", Pois = [MakePoi(1, "P1")] });
        _ops.Setup(s => s.CommitResultAsync(It.IsAny<List<Poi>>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("FK violated"));

        var vm = CreateVm();
        vm.CollectionAId = 1; vm.CollectionBId = 2;
        await vm.RunOperationAsync(SetOperation.Subtract);
        vm.CommitName = "MyName";

        // Must NOT throw — the circuit would otherwise be torn down.
        await vm.DoCommitAsync();

        vm.CommitSuccess.Should().Contain("FK violated");
    }
}
