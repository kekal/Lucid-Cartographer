namespace LucidCartographer.Tests.Integration
{
    [Collection("Integration")]
    public class SearchIntegrationTests : IntegrationTestBase
    {
        private async Task SeedSearchDataAsync()
        {
            await ImportTestFileAsync("sample.gpx", "Poland Places", "#005bbf");
        }

        [Fact]
        public async Task SearchBar_IsVisibleInHeader()
        {
            await NavigateAndWaitAsync("/");

            var searchInput = Page.Locator("input[aria-label='Search POIs...'][placeholder*='Search']");
            Assert.True(await searchInput.IsVisibleAsync(),
                "Search bar should be visible in the header");
        }

        [Fact]
        public async Task TypingQueryAndPressingEnter_NavigatesToSearchUrl()
        {
            await SeedSearchDataAsync();
            await NavigateAndWaitAsync("/");

            // Type in the real search bar and press Enter — this submits the HTML form.
            // MainLayout's search-form submit handler debounces navigation by 300ms, so we
            // wait for the URL to pick up the search parameter rather than reading it immediately.
            var searchInput = Page.Locator("input[aria-label='Search POIs...']");
            await searchInput.FillAsync("Wawel");
            await searchInput.PressAsync("Enter");

            await Page.WaitForURLAsync(new System.Text.RegularExpressions.Regex("search=Wawel"),
                new() { Timeout = 5000 });
            await Page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);

            var currentUrl = Page.Url;
            Assert.Contains("search=Wawel", currentUrl);
        }

        [Fact]
        public async Task SearchResults_ShowMatchingPois()
        {
            await SeedSearchDataAsync();
            await NavigateAndWaitAsync("/");

            // Use the actual search bar
            var searchInput = Page.Locator("input[aria-label='Search POIs...']");
            await searchInput.FillAsync("Wawel");
            await searchInput.PressAsync("Enter");

            await Page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);
            await Page.WaitForSelectorAsync("span:has-text('items')", new() { Timeout = 10000 });

            Assert.True(await Page.Locator("td:has-text('Wawel Castle')").IsVisibleAsync(),
                "Search for 'Wawel' should show Wawel Castle in results");
        }

        [Fact]
        public async Task SearchWithNoMatches_ShowsEmptyTable()
        {
            await NavigateAndWaitAsync("/");

            // Search for something that doesn't exist
            var searchInput = Page.Locator("input[aria-label='Search POIs...']");
            await searchInput.FillAsync("NonExistentPlace12345");
            await searchInput.PressAsync("Enter");

            await Page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);
            await Page.WaitForSelectorAsync("p:has-text('No POIs to display')", new() { Timeout = 10000 });

            Assert.True(await Page.Locator("p:has-text('No POIs to display')").IsVisibleAsync(),
                "Search with no matches should show empty table message");
        }
    }
}
