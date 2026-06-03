using FluentAssertions;
using LucidCartographer.Services;
using LucidCartographer.Services.Browser;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LucidCartographer.Tests.Browser;

public class BrowserSessionManagerTests
{
    private const string EnvVar = "CHROME_PROFILE_PATH";

    private static BrowserSessionManager Create(BrowserOptions opts)
        => new(
            NullLogger<BrowserSessionManager>.Instance,
            new GoogleBrowserLock(),
            Options.Create(opts));

    [Fact]
    public void ProfilePath_PrefersEnvVar_OverConfigAndDefault()
    {
        var prev = Environment.GetEnvironmentVariable(EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(EnvVar, "/data/chrome-profile");
            var sut = Create(new BrowserOptions { ProfilePath = "/from/config" });
            sut.ProfilePath.Should().Be("/data/chrome-profile");
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, prev);
        }
    }

    [Fact]
    public void ProfilePath_UsesConfig_WhenEnvAbsent()
    {
        var prev = Environment.GetEnvironmentVariable(EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(EnvVar, null);
            var sut = Create(new BrowserOptions { ProfilePath = "/from/config" });
            sut.ProfilePath.Should().Be("/from/config");
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, prev);
        }
    }

    [Fact]
    public void ProfilePath_FallsBackToAppBaseDefault_WhenNeitherSet()
    {
        var prev = Environment.GetEnvironmentVariable(EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(EnvVar, null);
            var sut = Create(new BrowserOptions { ProfilePath = null });
            sut.ProfilePath.Should().Be(Path.Combine(AppContext.BaseDirectory, "data", "chrome-profile"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, prev);
        }
    }

    [Fact]
    public void HasProfile_False_WhenDirectoryMissing()
    {
        var prev = Environment.GetEnvironmentVariable(EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(EnvVar,
                Path.Combine(Path.GetTempPath(), $"cartographer_noprofile_{Guid.NewGuid():N}"));
            var sut = Create(new BrowserOptions());
            sut.HasProfile.Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, prev);
        }
    }
}
