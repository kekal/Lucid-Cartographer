using FluentAssertions;
using LucidCartographer.Services.Enrichment;

namespace LucidCartographer.Tests;

public class EnrichmentResultPickerTests
{
    [Fact]
    public void PicksTheSingleUnambiguousMatch_RealWorldExample()
    {
        // The motivating case: searching "Park Dzikich Zwierząt Kadzidłowo"
        // returns the park itself plus its ticket office. A human knows which
        // is which; the picker keys on name similarity and takes the one card
        // that clearly matches, leaving the ticket office out.
        var candidates = new[]
        {
            "Park Dzikich Zwierząt",        // the place
            "Kasa Parku Dzikich Zwierząt"   // the ticket office
        };

        EnrichmentResultPicker
            .PickUnambiguousMatch("Park Dzikich Zwierząt Kadzidłowo", candidates)
            .Should().Be(0);
    }

    [Fact]
    public void ReturnsNull_WhenTwoCardsBothMatch()
    {
        // Two equally-good matches → ambiguous → defer to the manual dialog.
        var candidates = new[] { "Coffee Shop", "Coffee Shop" };

        EnrichmentResultPicker
            .PickUnambiguousMatch("Coffee Shop", candidates)
            .Should().BeNull();
    }

    [Fact]
    public void ReturnsNull_WhenNoCardMatches()
    {
        var candidates = new[] { "Dentist", "Pharmacy", "Hardware Store" };

        EnrichmentResultPicker
            .PickUnambiguousMatch("Wawel Royal Castle", candidates)
            .Should().BeNull();
    }

    [Fact]
    public void IgnoresBlankCandidateNames()
    {
        var candidates = new[] { "", "   ", "Wawel Royal Castle" };

        EnrichmentResultPicker
            .PickUnambiguousMatch("Wawel Royal Castle", candidates)
            .Should().Be(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ReturnsNull_WhenTargetNameIsBlank(string? target)
    {
        var candidates = new[] { "Anything" };

        EnrichmentResultPicker
            .PickUnambiguousMatch(target!, candidates)
            .Should().BeNull();
    }

    [Fact]
    public void ReturnsNull_OnEmptyCandidateList()
    {
        EnrichmentResultPicker
            .PickUnambiguousMatch("Anything", Array.Empty<string>())
            .Should().BeNull();
    }

    [Fact]
    public void BorderlineMatch_BelowThreshold_DefersToManual()
    {
        // A loosely-related name that clears the lenient dedup bar (0.6) but
        // not the stricter auto-select bar (0.8) must NOT be auto-picked.
        var candidates = new[] { "Kasa Parku Dzikich Zwierząt" };

        EnrichmentResultPicker
            .PickUnambiguousMatch("Park Dzikich Zwierząt Kadzidłowo", candidates)
            .Should().BeNull();
    }
}
