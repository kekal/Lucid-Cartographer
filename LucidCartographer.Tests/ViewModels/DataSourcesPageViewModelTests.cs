using FluentAssertions;
using LucidCartographer.Components.Pages;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Enrichment;
using LucidCartographer.Services.Export;
using LucidCartographer.Services.Import;
using Microsoft.JSInterop;
using Moq;

namespace LucidCartographer.Tests.ViewModels;

/// <summary>
/// Plain xUnit tests for the Data Sources page VM. Mocks all dependencies
/// and asserts state mutations + service calls without rendering the
/// component.
/// </summary>
public class DataSourcesPageViewModelTests
{
    private readonly Mock<IImportJobQueue> _queue = new();
    private readonly Mock<IPoiService> _poi = new();
    private readonly Mock<IGoogleMapsListScraper> _scraper = new();
    private readonly Mock<IFileExporter> _exporter = new();
    private readonly Mock<IJSRuntime> _js = new();
    private readonly EnrichmentTrigger _trigger = new();

    // ImportJobStatusService is concrete and depends on a BehaviorSubject;
    // construct it directly with a deterministic seed.
    private readonly ImportJobStatusService _status = new();

    private DataSourcesPageViewModel CreateVm()
    {
        _exporter.SetupGet(e => e.FormatName).Returns("KML");
        return new DataSourcesPageViewModel(
            _queue.Object, _status, _poi.Object, _scraper.Object,
            [_exporter.Object], _js.Object, _trigger);
    }

    private static PoiCollection MakeCollection(int id, string name, string color = "#005bbf")
        => new() { Id = id, Name = name, Color = color };

    [Fact]
    public async Task Initialize_LoadsCollections_AndSetsBaselineState()
    {
        _poi.Setup(p => p.GetCollectionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<PoiCollection>)[MakeCollection(1, "A")]);
        _poi.Setup(p => p.GetFailedEnrichmentCountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);

        var vm = CreateVm();
        await vm.InitializeAsync();

        vm.Collections.Should().HaveCount(1);
        vm.FailedEnrichmentCount.Should().Be(3);
        vm.IsImporting.Should().BeFalse();
    }

    [Fact]
    public void ShowUploadFor_TogglesPanel_ResetsResultMessages()
    {
        var vm = CreateVm();
        vm.ShowUploadFor("takeout");

        vm.ShowUpload.Should().BeTrue();
        vm.ActiveCard.Should().Be("takeout");
        vm.ImportResult.Should().BeNull();
        vm.ImportError.Should().BeNull();
    }

    [Fact]
    public void CloseUpload_HidesPanel()
    {
        var vm = CreateVm();
        vm.ShowUploadFor("file");
        vm.CloseUpload();

        vm.ShowUpload.Should().BeFalse();
    }

    [Fact]
    public void UploadTitle_VariesByActiveCard()
    {
        var vm = CreateVm();
        vm.ShowUploadFor("file");
        var fileTitle = vm.UploadTitle;

        vm.ShowUploadFor("takeout");
        var takeoutTitle = vm.UploadTitle;

        vm.ShowUploadFor("shared");
        var sharedTitle = vm.UploadTitle;

        fileTitle.Should().NotBe(takeoutTitle);
        takeoutTitle.Should().NotBe(sharedTitle);
    }

    [Fact]
    public void OpenAddPoi_AndCloseAddPoi_ToggleModalState()
    {
        var vm = CreateVm();

        vm.OpenAddPoi(7);
        vm.AddPoiCollectionId.Should().Be(7);
        vm.AddPoiUrl.Should().BeEmpty();

        vm.CloseAddPoi();
        vm.AddPoiCollectionId.Should().BeNull();
    }

    [Fact]
    public async Task SaveNewPoi_RejectsEmptyUrl_WithErrorMessage()
    {
        var vm = CreateVm();
        vm.OpenAddPoi(1);
        vm.AddPoiUrl = "";

        await vm.SaveNewPoiAsync();

        vm.AddPoiError.Should().NotBeNullOrEmpty();
        vm.AddPoiSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task SaveNewPoi_RejectsNonHttpUrl_WithErrorMessage()
    {
        var vm = CreateVm();
        vm.OpenAddPoi(1);
        vm.AddPoiUrl = "ftp://example.com/place";

        await vm.SaveNewPoiAsync();

        vm.AddPoiError.Should().NotBeNullOrEmpty();
        vm.AddPoiSuccess.Should().BeFalse();
    }

    [Fact]
    public void RequestDelete_AndCancelDelete_TogglePendingState()
    {
        var vm = CreateVm();

        vm.RequestDelete(5);
        vm.PendingDeleteId.Should().Be(5);

        vm.CancelDelete();
        vm.PendingDeleteId.Should().BeNull();
    }

    [Fact]
    public void OpenColorPicker_PrefillsFromCollection_WhenColorSet()
    {
        var vm = CreateVm();
        var col = MakeCollection(11, "X", "#abcdef");

        vm.OpenColorPicker(col);

        vm.ColorPickerCollectionId.Should().Be(11);
        vm.ColorPickerValue.Should().Be("#abcdef");
        vm.ColorPickerError.Should().BeNull();
    }

    [Fact]
    public void OpenColorPicker_FallsBackToDefault_WhenColorMissing()
    {
        var vm = CreateVm();
        var col = MakeCollection(11, "X", "");

        vm.OpenColorPicker(col);

        vm.ColorPickerValue.Should().Be("#005bbf");
    }

    [Fact]
    public async Task ConfirmDelete_DispatchesToService_AndReloads()
    {
        _poi.Setup(p => p.GetCollectionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _poi.Setup(p => p.GetFailedEnrichmentCountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var vm = CreateVm();
        vm.RequestDelete(7);

        await vm.ConfirmDeleteAsync(7);

        vm.PendingDeleteId.Should().BeNull();
        _poi.Verify(p => p.DeleteCollectionAsync(7, It.IsAny<CancellationToken>()), Times.Once);
    }
}
