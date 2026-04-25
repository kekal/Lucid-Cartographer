using LucidCartographer.Services.Import;
using Microsoft.Extensions.DependencyInjection;

namespace LucidCartographer.Tests.Integration;

/// <summary>
/// Base class for tests that need a fake scraper instead of the real one.
/// Extends IntegrationTestBase and overrides service registration to use
/// FakeGoogleMapsListScraper as IGoogleMapsListScraper.
/// </summary>
public abstract class ScraperTestBase : IntegrationTestBase
{
    protected FakeGoogleMapsListScraper FakeScraper = new();

    protected override void RegisterAdditionalServices(IServiceCollection services)
    {
        // Register the FAKE scraper instead of the real one
        services.AddSingleton<IGoogleMapsListScraper>(FakeScraper);
    }
}