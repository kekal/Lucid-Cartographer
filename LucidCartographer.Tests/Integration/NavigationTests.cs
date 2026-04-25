namespace LucidCartographer.Tests.Integration;

[Collection("Integration")]
public class NavigationTests : IntegrationTestBase
{
    [Fact]
    public async Task HomePage_ShowsLucidCartographerTitle()
    {
        await NavigateAndWaitAsync("/");

        var titleText = await Page.Locator("h1").InnerTextAsync();
        Assert.Contains("Lucid Cartographer", titleText);
    }

    [Fact]
    public async Task HomePage_RendersLeafletMap()
    {
        await NavigateAndWaitAsync("/");

        var mapCount = await Page.Locator("[id^='leaflet-map']").CountAsync();
        Assert.True(mapCount > 0, "Leaflet map container should be visible on the home page");
    }

    [Fact]
    public async Task ClickingDataSourcesTab_NavigatesToDataSourcesPage()
    {
        await NavigateAndWaitAsync("/");

        await Page.Locator("a:has-text('Data Sources')").ClickAsync();
        await Page.WaitForURLAsync("**/datasources");
        // Verify the Data Sources page content is actually rendered
        await Page.WaitForSelectorAsync("h2:has-text('Data & Imports')", new() { Timeout = 10000 });

        Assert.Contains("/datasources", Page.Url);
    }

    [Fact]
    public async Task ClickingOperationsTab_NavigatesToOperationsPage()
    {
        await NavigateAndWaitAsync("/");

        await Page.Locator("a:has-text('Operations')").ClickAsync();
        await Page.WaitForURLAsync("**/operations");
        // Verify the Operations page content is actually rendered
        await Page.WaitForSelectorAsync("h3:has-text('Source Selection')", new() { Timeout = 10000 });

        Assert.Contains("/operations", Page.Url);
    }

    [Fact]
    public async Task ClickingMapTab_NavigatesBackToHomePage()
    {
        await NavigateAndWaitAsync("/");
        await Page.Locator("nav a:has-text('Data Sources')").ClickAsync();
        await Page.WaitForURLAsync("**/datasources");
        await Page.WaitForSelectorAsync("h2:has-text('Data & Imports')", new() { Timeout = 10000 });

        await Page.Locator("a:has-text('Map')").ClickAsync();
        await Page.WaitForURLAsync(url => !url.Contains("/datasources") && !url.Contains("/operations"));

        // The map page is at the root
        var uri = new Uri(Page.Url);
        Assert.Equal("/", uri.AbsolutePath);
    }

    [Fact]
    public async Task AllThreeTabsAreVisibleInHeader()
    {
        await NavigateAndWaitAsync("/");

        Assert.True(await Page.Locator("header a:has-text('Map')").IsVisibleAsync(),
            "Map tab should be visible");
        Assert.True(await Page.Locator("header a:has-text('Data Sources')").IsVisibleAsync(),
            "Data Sources tab should be visible");
        Assert.True(await Page.Locator("header a:has-text('Operations')").IsVisibleAsync(),
            "Operations tab should be visible");
    }
}