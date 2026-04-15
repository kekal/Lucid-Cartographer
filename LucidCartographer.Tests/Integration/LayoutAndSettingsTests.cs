namespace LucidCartographer.Tests.Integration
{
    /// <summary>
    /// Tests for layout, navigation, settings, and global UI elements.
    /// Verifies page title changes, navigation structure, search placeholder, and settings button.
    /// </summary>
    [Collection("Integration")]
    public class LayoutAndSettingsTests : IntegrationTestBase
    {
        [Fact]
        public async Task MainLayout_LogoutLink_IsVisible()
        {
            await NavigateAndWaitAsync("/");

            // LOW-06: Settings button was removed; verify logout link exists instead
            var logoutLink = Page.Locator("a[aria-label='Logout']");

            Assert.True(await logoutLink.IsVisibleAsync(),
                "Logout link should be visible in the header");
        }

        [Fact]
        public async Task MainLayout_NavTabs_AllThreePresent()
        {
            await NavigateAndWaitAsync("/");

            // Locate all three nav tabs
            var mapTab = Page.Locator("nav a:has-text('Map')");
            var dataSourcesTab = Page.Locator("nav a:has-text('Data Sources')");
            var operationsTab = Page.Locator("nav a:has-text('Operations')");

            Assert.True(await mapTab.IsVisibleAsync(), "Map tab should be visible");
            Assert.True(await dataSourcesTab.IsVisibleAsync(), "Data Sources tab should be visible");
            Assert.True(await operationsTab.IsVisibleAsync(), "Operations tab should be visible");
        }

        [Fact]
        public async Task MainLayout_SearchInput_HasCorrectPlaceholder()
        {
            await NavigateAndWaitAsync("/");

            // Locate the search input
            var searchInput = Page.Locator("input[aria-label='Search POIs...']");

            Assert.True(await searchInput.IsVisibleAsync(),
                "Search input should be visible");

            var placeholder = await searchInput.GetAttributeAsync("placeholder");
            Assert.Equal("Search POIs...", placeholder);
        }

        [Fact]
        public async Task PageTitle_ChangesOnMapPageNavigation()
        {
            await NavigateAndWaitAsync("/");

            // Verify initial page title on Map page
            var title = await Page.TitleAsync();
            Assert.True(title.Contains("Map") && title.Contains("Lucid Cartographer"),
                "Page title should contain 'Map' and 'Lucid Cartographer'");
        }

        [Fact]
        public async Task PageTitle_ChangesOnDataSourcesNavigation()
        {
            await NavigateAndWaitAsync("/");
            await ClickDataSourcesTabAsync();

            var title = await Page.TitleAsync();
            Assert.True(title.Contains("Data Sources") && title.Contains("Lucid Cartographer"),
                "Page title should contain 'Data Sources' and 'Lucid Cartographer'");
        }

        [Fact]
        public async Task PageTitle_ChangesOnOperationsNavigation()
        {
            await NavigateAndWaitAsync("/");
            await ClickOperationsTabAsync();

            var title = await Page.TitleAsync();
            Assert.True(title.Contains("Operations") && title.Contains("Lucid Cartographer"),
                "Page title should contain 'Operations' and 'Lucid Cartographer'");
        }

        [Fact]
        public async Task Navigation_MapTabActive_OnMapPage()
        {
            await NavigateAndWaitAsync("/");

            var mapTab = Page.Locator("nav a:has-text('Map')");
            var classes = await mapTab.GetAttributeAsync("class");
            var ariaAttr = await mapTab.GetAttributeAsync("aria-current");

            // Verify at least ONE active indicator: text-primary class OR aria-current="page"
            var hasActiveClass = classes != null && classes.Contains("text-primary");
            var hasAriaCurrent = ariaAttr == "page";

            Assert.True(
                hasActiveClass || hasAriaCurrent,
                $"Map tab should have active indicator. Classes: '{classes}', aria-current: '{ariaAttr}'");

            // Also verify inactive tabs do NOT have active indicators
            var dataSourcesTab = Page.Locator("nav a:has-text('Data Sources')");
            var dsClasses = await dataSourcesTab.GetAttributeAsync("class");
            var dsAria = await dataSourcesTab.GetAttributeAsync("aria-current");
            var dsIsActive = dsClasses != null && dsClasses.Contains("text-primary") || dsAria == "page";

            Assert.False(dsIsActive,
                "Data Sources tab should NOT be active on Map page");
        }

        [Fact]
        public async Task Navigation_DataSourcesTabActive_OnDataSourcesPage()
        {
            await NavigateAndWaitAsync("/");
            await ClickDataSourcesTabAsync();

            var dataSourcesTab = Page.Locator("nav a:has-text('Data Sources')");
            var classes = await dataSourcesTab.GetAttributeAsync("class");
            var ariaAttr = await dataSourcesTab.GetAttributeAsync("aria-current");

            // Verify at least ONE active indicator
            var hasActiveClass = classes != null && classes.Contains("text-primary");
            var hasAriaCurrent = ariaAttr == "page";

            Assert.True(
                hasActiveClass || hasAriaCurrent,
                $"Data Sources tab should be visually active. Classes: '{classes}', aria-current: '{ariaAttr}'");

            // Verify other tabs are NOT active
            var mapTab = Page.Locator("nav a:has-text('Map')");
            var mapClasses = await mapTab.GetAttributeAsync("class");
            var mapAria = await mapTab.GetAttributeAsync("aria-current");
            var mapIsActive = mapClasses != null && mapClasses.Contains("text-primary") || mapAria == "page";

            Assert.False(mapIsActive,
                "Map tab should NOT be active on DataSources page");
        }

        [Fact]
        public async Task Navigation_OperationsTabActive_OnOperationsPage()
        {
            await NavigateAndWaitAsync("/");
            await ClickOperationsTabAsync();

            var operationsTab = Page.Locator("nav a:has-text('Operations')");
            var classes = await operationsTab.GetAttributeAsync("class");
            var ariaAttr = await operationsTab.GetAttributeAsync("aria-current");

            // Verify at least ONE active indicator
            var hasActiveClass = classes != null && classes.Contains("text-primary");
            var hasAriaCurrent = ariaAttr == "page";

            Assert.True(
                hasActiveClass || hasAriaCurrent,
                $"Operations tab should be visually active. Classes: '{classes}', aria-current: '{ariaAttr}'");

            // Verify other tabs are NOT active
            var mapTab = Page.Locator("nav a:has-text('Map')");
            var mapClasses = await mapTab.GetAttributeAsync("class");
            var mapAria = await mapTab.GetAttributeAsync("aria-current");
            var mapIsActive = mapClasses != null && mapClasses.Contains("text-primary") || mapAria == "page";

            Assert.False(mapIsActive,
                "Map tab should NOT be active on Operations page");
        }

        [Fact]
        public async Task HeaderLayout_AllElementsPresent()
        {
            await NavigateAndWaitAsync("/");

            // Verify header structure: title, nav tabs, search, logout
            var title = Page.Locator("h1:has-text('Lucid Cartographer')");
            var searchInput = Page.Locator("input[aria-label='Search POIs...']");
            var logoutLink = Page.Locator("a[aria-label='Logout']");
            var navLinks = Page.Locator("nav a");

            Assert.True(await title.IsVisibleAsync(), "App title 'Lucid Cartographer' should be visible");
            Assert.True(await searchInput.IsVisibleAsync(), "Search input should be visible");
            Assert.True(await logoutLink.IsVisibleAsync(), "Logout link should be visible");

            var navCount = await navLinks.CountAsync();
            Assert.True(navCount >= 3, $"Should have at least 3 navigation links, found {navCount}");

            // Verify each tab is actually clickable and present
            var mapTab = Page.Locator("nav a:has-text('Map')");
            var dataSourcesTab = Page.Locator("nav a:has-text('Data Sources')");
            var operationsTab = Page.Locator("nav a:has-text('Operations')");

            Assert.True(await mapTab.IsVisibleAsync(), "Map tab should be present");
            Assert.True(await dataSourcesTab.IsVisibleAsync(), "Data Sources tab should be present");
            Assert.True(await operationsTab.IsVisibleAsync(), "Operations tab should be present");
        }

        [Fact]
        public async Task SearchInput_CanReceiveFocus()
        {
            await NavigateAndWaitAsync("/");

            var searchInput = Page.Locator("input[aria-label='Search POIs...']");
            await searchInput.ClickAsync();

            // Verify it has focus by checking if typing works
            await searchInput.FillAsync("test");
            var value = await searchInput.InputValueAsync();

            Assert.Equal("test", value);
        }
    }
}
