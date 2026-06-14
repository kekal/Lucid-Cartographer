using Bunit;
using BunitTestContext = Bunit.TestContext;
using FluentAssertions;
using LucidCartographer.Components.Shared.Trip;
using LucidCartographer.Data;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;
using LucidCartographer.Services.Trip;
using LucidCartographer.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LucidCartographer.Tests.Components;

public class TripToggleTests : BunitTestContext
{
    private const int CollectionId = 1;

    private static IDbContextFactory<AppDbContext> SeedFactory(int placeable)
    {
        var factory = TestDbHelper.CreateFactory();
        using var db = factory.CreateDbContext();
        db.PoiCollections.Add(new PoiCollection { Id = CollectionId, Name = "Trip", Color = "#005bbf" });
        for (var i = 1; i <= placeable; i++)
        {
            db.Pois.Add(new Poi { Id = i, Name = $"P{i}", Latitude = 50, Longitude = 20, AddedDate = new DateTime(2025, 1, i) });
            db.PoiCollectionItems.Add(new PoiCollectionItem { PoiId = i, PoiCollectionId = CollectionId });
        }
        db.SaveChanges();
        return factory;
    }

    private static TripViewModel CreateVm(IDbContextFactory<AppDbContext> factory)
    {
        var writeLock = new SqliteWriteLock();
        var ordering = new TripOrderingService(factory, writeLock, NullLogger<TripOrderingService>.Instance);
        return new TripViewModel(ordering, factory, writeLock, new TravelTimeTrigger(), new TravelTimeProgressService(), TestDbHelper.CreateInvalidationService(factory), NullLogger<TripViewModel>.Instance);
    }

    [Fact]
    public async Task Renders_SwitchWithAriaPressedFalse_WhenAvailableAndOff()
    {
        var vm = CreateVm(SeedFactory(placeable: 2));
        await vm.LoadAsync(CollectionId, 2);

        var cut = RenderComponent<TripToggle>(p => p.Add(x => x.Vm, vm));

        var toggle = cut.Find("button[role='switch']");
        toggle.GetAttribute("aria-pressed").Should().Be("false");
        toggle.GetAttribute("aria-label").Should().Be(UiStrings.TripViewToggleAria);
        cut.Markup.Should().Contain(UiStrings.TripView);
    }

    [Fact]
    public async Task IsHidden_WhenFewerThanTwoPlaceable()
    {
        var vm = CreateVm(SeedFactory(placeable: 1));
        await vm.LoadAsync(CollectionId, 1);

        var cut = RenderComponent<TripToggle>(p => p.Add(x => x.Vm, vm));

        cut.FindAll("button[role='switch']").Should().BeEmpty("the toggle is absent below the ≥2 gate, never an error");
    }

    [Fact]
    public async Task Toggling_FlipsAriaPressed_AndAnnouncesOnState()
    {
        var vm = CreateVm(SeedFactory(placeable: 2));
        await vm.LoadAsync(CollectionId, 2);
        var cut = RenderComponent<TripToggle>(p => p.Add(x => x.Vm, vm));

        await cut.Find("button[role='switch']").ClickAsync(new());

        cut.WaitForAssertion(() =>
        {
            cut.Find("button[role='switch']").GetAttribute("aria-pressed").Should().Be("true");
            cut.Find("[role='status']").TextContent.Should().Contain(UiStrings.TripViewEnabledAnnouncement);
        });
    }

    [Fact]
    public void StopOrderBadge_RendersNumber_WithPrimaryFill_AndAriaLabel()
    {
        var cut = RenderComponent<StopOrderBadge>(p => p.Add(x => x.Number, 3));

        var badge = cut.Find("span");
        badge.TextContent.Trim().Should().Be("3");
        badge.ClassList.Should().Contain("bg-primary");
        badge.ClassList.Should().Contain("text-on-primary");
        badge.GetAttribute("aria-label").Should().Be(string.Format(System.Globalization.CultureInfo.CurrentCulture, UiStrings.StopOrderBadgeAria, 3));
    }
}
