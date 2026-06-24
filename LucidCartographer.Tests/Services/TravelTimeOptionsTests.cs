using FluentAssertions;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services.Trip;

namespace LucidCartographer.Tests;

/// <summary>
/// Story 1.1: per-mode detour/winding factors on <see cref="TravelTimeOptions"/>.
/// The accessor mirrors <c>SpeedFor</c>; Any/Air returns 1.0 (no winding).
/// </summary>
public class TravelTimeOptionsTests
{
    [Fact]
    public void DetourFactor_ShippedDefaults_AreDocumentedAssumptions()
    {
        var options = new TravelTimeOptions();

        options.DriveDetourFactor.Should().Be(1.3);
        options.CycleDetourFactor.Should().Be(1.2);
        options.WalkDetourFactor.Should().Be(1.15);
    }

    [Theory]
    [InlineData(TravelMode.Drive)]
    [InlineData(TravelMode.Cycle)]
    [InlineData(TravelMode.Walk)]
    public void DetourFactorFor_GroundMode_ReturnsDefault(string mode)
    {
        var options = new TravelTimeOptions();

        var expected = mode switch
        {
            TravelMode.Drive => 1.3,
            TravelMode.Cycle => 1.2,
            TravelMode.Walk => 1.15,
            _ => 1.0,
        };

        options.DetourFactorFor(mode).Should().Be(expected);
    }

    [Theory]
    [InlineData(TravelMode.Drive)]
    [InlineData(TravelMode.Cycle)]
    [InlineData(TravelMode.Walk)]
    public void DetourFactorFor_GroundMode_ReturnsConfiguredValue(string mode)
    {
        var options = new TravelTimeOptions
        {
            DriveDetourFactor = 2.0,
            CycleDetourFactor = 2.5,
            WalkDetourFactor = 3.0,
        };

        var expected = mode switch
        {
            TravelMode.Drive => 2.0,
            TravelMode.Cycle => 2.5,
            TravelMode.Walk => 3.0,
            _ => 1.0,
        };

        options.DetourFactorFor(mode).Should().Be(expected);
    }

    [Fact]
    public void DetourFactorFor_AnyAir_ReturnsOne()
    {
        var options = new TravelTimeOptions { DriveDetourFactor = 9.0 };

        options.DetourFactorFor(TravelMode.AnyAir).Should().Be(1.0);
    }
}
