using Bunit;
using BunitTestContext = Bunit.TestContext;
using FluentAssertions;
using LucidCartographer.Components.Shared;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace LucidCartographer.Tests.Components;

public class LeafletMapTests : BunitTestContext
{
    private readonly Mock<IMapService> _mockMapService;

    public LeafletMapTests()
    {
        _mockMapService = new Mock<IMapService>();
        _mockMapService.Setup(m => m.InitMapAsync(It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _mockMapService.Setup(m => m.ShowCollectionAsync(It.IsAny<int>(), It.IsAny<List<Poi>>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _mockMapService.Setup(m => m.HideCollectionAsync(It.IsAny<int>()))
            .Returns(Task.CompletedTask);
        _mockMapService.Setup(m => m.FocusOnPoiAsync(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<int>()))
            .Returns(Task.CompletedTask);
        _mockMapService.Setup(m => m.FitBoundsAsync())
            .Returns(Task.CompletedTask);
        _mockMapService.Setup(m => m.HighlightMarkerAsync(It.IsAny<int>()))
            .Returns(Task.CompletedTask);

        Services.AddSingleton<IMapService>(_mockMapService.Object);
    }

    [Fact]
    public void Component_RendersDiv_WithDynamicLeafletMapId()
    {
        var cut = RenderComponent<LeafletMap>();

        // LOW-06: Map element ID is now dynamically generated (leaflet-map-{guid})
        var mapDiv = cut.Find("div[id^='leaflet-map-']");
        mapDiv.Should().NotBeNull();
        mapDiv.GetAttribute("style").Should().Contain("width:100%");
        mapDiv.GetAttribute("style").Should().Contain("height:100%");
    }

    [Fact]
    public void AfterFirstRender_InitMapAsync_IsCalledOnMapService()
    {
        var cut = RenderComponent<LeafletMap>();

        // LOW-06: Map element ID is now dynamically generated
        _mockMapService.Verify(m => m.InitMapAsync(It.Is<string>(s => s.StartsWith("leaflet-map-"))), Times.Once);
    }

    [Fact]
    public async Task ShowCollectionAsync_DelegatesToMapService()
    {
        var cut = RenderComponent<LeafletMap>();
        List<Poi> pois = [new() { Id = 1, Name = "A", Latitude = 10, Longitude = 20 }];

        await cut.Instance.ShowCollectionAsync(5, pois, "#ff0000");

        _mockMapService.Verify(m => m.ShowCollectionAsync(5, pois, "#ff0000"), Times.Once);
    }

    [Fact]
    public async Task HideCollectionAsync_DelegatesToMapService()
    {
        var cut = RenderComponent<LeafletMap>();

        await cut.Instance.HideCollectionAsync(3);

        _mockMapService.Verify(m => m.HideCollectionAsync(3), Times.Once);
    }

    [Fact]
    public async Task FocusOnPoiAsync_DelegatesToMapService()
    {
        var cut = RenderComponent<LeafletMap>();

        await cut.Instance.FocusOnPoiAsync(51.5, -0.12);

        _mockMapService.Verify(m => m.FocusOnPoiAsync(51.5, -0.12, 16), Times.Once);
    }
}