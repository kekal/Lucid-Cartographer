using Bunit;
using BunitTestContext = Bunit.TestContext;
using FluentAssertions;
using LucidCartographer.Components.Shared.Trip;
using LucidCartographer.Data.Entities;
using LucidCartographer.Services;

namespace LucidCartographer.Tests.Components;

/// <summary>
/// Story 2.3 (FR-7, UX-DR9): the fidelity badge is self-explaining. For each of
/// Measured/Estimated/Manual the badge's <c>title</c> AND <c>aria-label</c> both
/// equal the plain-language tooltip (parity, NFR7), while the visible text stays
/// the short word. Placeholder/null render no badge at all.
/// </summary>
public class FidelityBadgeTests : BunitTestContext
{
    [Theory]
    [InlineData(Fidelity.Measured, UiStrings.TripFidelityMeasured, UiStrings.TripFidelityMeasuredTooltip)]
    [InlineData(Fidelity.Estimated, UiStrings.TripFidelityEstimated, UiStrings.TripFidelityEstimatedTooltip)]
    [InlineData(Fidelity.Manual, UiStrings.TripFidelityManual, UiStrings.TripFidelityManualTooltip)]
    public void Badge_TitleAndAriaLabel_AreThePlainLanguageTooltip_WhileVisibleTextStaysShortWord(
        string fidelity, string shortWord, string tooltip)
    {
        var cut = RenderComponent<FidelityBadge>(p => p.Add(x => x.Fidelity, fidelity));

        var span = cut.Find("span");
        // Visible text is unchanged — still the short word.
        span.TextContent.Trim().Should().Be(shortWord);
        // title and aria-label are at parity, both the plain-language tooltip (NFR7).
        span.GetAttribute("title").Should().Be(tooltip);
        span.GetAttribute("aria-label").Should().Be(tooltip);
        // Sanity: the circular "Provenance: …" copy is gone.
        span.GetAttribute("title").Should().NotContain("Provenance:");
        span.GetAttribute("aria-label").Should().NotContain("Provenance:");
    }

    [Theory]
    [InlineData(Fidelity.Placeholder)]
    [InlineData(null)]
    public void Badge_PlaceholderOrNull_RendersNothing(string? fidelity)
    {
        var cut = RenderComponent<FidelityBadge>(p => p.Add(x => x.Fidelity, fidelity));

        cut.Markup.Trim().Should().BeEmpty("Placeholder/null never render a badge");
    }
}
