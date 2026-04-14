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

            // Check if the active class or styling indicates it's active
            // NavLink adds "active" class or border styling when active
            var ariaAttr = await mapTab.GetAttributeAsync("aria-current");

            Assert.True(
                classes != null && classes.Contains("text-primary") ||
                ariaAttr == "page",
                "Map tab should be visually active with primary color or aria-current");
        }

        [Fact]
        public async Task Navigation_DataSourcesTabActive_OnDataSourcesPage()
        {
            await NavigateAndWaitAsync("/");
            await ClickDataSourcesTabAsync();

            var dataSourcesTab = Page.Locator("nav a:has-text('Data Sources')");
            var classes = await dataSourcesTab.GetAttributeAsync("class");
            var ariaAttr = await dataSourcesTab.GetAttributeAsync("aria-current");

            Assert.True(
                classes != null && classes.Contains("text-primary") ||
                ariaAttr == "page",
                "Data Sources tab should be visually active");
        }

        [Fact]
        public async Task Navigation_OperationsTabActive_OnOperationsPage()
        {
            await NavigateAndWaitAsync("/");
            await ClickOperationsTabAsync();

            var operationsTab = Page.Locator("nav a:has-text('Operations')");
            var classes = await operationsTab.GetAttributeAsync("class");
            var ariaAttr = await operationsTab.GetAttributeAsync("aria-current");

            Assert.True(
                classes != null && classes.Contains("text-primary") ||
                ariaAttr == "page",
                "Operations tab should be visually active");
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

            Assert.True(await title.IsVisibleAsync(), "App title should be visible");
            Assert.True(await searchInput.IsVisibleAsync(), "Search input should be visible");
            Assert.True(await logoutLink.IsVisibleAsync(), "Logout link should be visible");

            var navCount = await navLinks.CountAsync();
            Assert.True(navCount >= 3, "Should have at least 3 navigation links");
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
