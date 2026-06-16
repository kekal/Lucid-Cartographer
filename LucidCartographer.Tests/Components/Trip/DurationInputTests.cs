using Bunit;
using BunitTestContext = Bunit.TestContext;
using FluentAssertions;
using LucidCartographer.Components.Shared.Trip;
using LucidCartographer.Services;
using Microsoft.AspNetCore.Components.Web;

namespace LucidCartographer.Tests.Components;

/// <summary>
/// Trip stops compaction (D3/D5): the reusable HH:MM duration control. It shows the
/// canonical minutes as an uncapped "HH:MM" and raises ValueChanged with new canonical
/// minutes — from typing (strict parse), from the ▲▼ steppers (±Step, Shift ±ShiftStep),
/// and from ArrowUp/ArrowDown — always clamped to [0, Max]. No JS: ShiftKey is read off
/// the Blazor event.
/// </summary>
public class DurationInputTests : BunitTestContext
{
    private static string Up => UiStrings.TripDurationStepUpAria;
    private static string Down => UiStrings.TripDurationStepDownAria;

    [Fact]
    public void Renders_CanonicalMinutes_AsHhmm()
    {
        var cut = RenderComponent<DurationInput>(p => p.Add(x => x.Value, 125));
        cut.Find("input").GetAttribute("value").Should().Be("02:05");
    }

    [Fact]
    public void Empty_When_ValueNull()
    {
        var cut = RenderComponent<DurationInput>(p => p.Add(x => x.Value, (int?)null));
        cut.Find("input").GetAttribute("value").Should().BeNullOrEmpty();
    }

    [Fact]
    public void Typing_ValidHhmm_RaisesCanonicalMinutes()
    {
        int? captured = -1;
        var cut = RenderComponent<DurationInput>(p => p
            .Add(x => x.Value, 0)
            .Add(x => x.ValueChanged, (int? v) => captured = v));

        cut.Find("input").Change("01:30");
        captured.Should().Be(90);
    }

    [Fact]
    public void Typing_OverDay_IsAccepted_Uncapped()
    {
        int? captured = -1;
        var cut = RenderComponent<DurationInput>(p => p
            .Add(x => x.Value, 0)
            .Add(x => x.ValueChanged, (int? v) => captured = v));

        cut.Find("input").Change("48:00");
        captured.Should().Be(2880);
    }

    [Fact]
    public void Typing_Invalid_RaisesNothing()
    {
        var raised = 0;
        var cut = RenderComponent<DurationInput>(p => p
            .Add(x => x.Value, 90)
            .Add(x => x.ValueChanged, (int? _) => raised++));

        cut.Find("input").Change("90"); // no colon ⇒ rejected
        raised.Should().Be(0, "a malformed entry never writes");
    }

    [Fact]
    public void Typing_Invalid_RevertsFieldToLastGoodValue()
    {
        // Review finding (re-key fix): a rejected entry must not linger in the DOM — the
        // field snaps back to the canonical @Display.
        var cut = RenderComponent<DurationInput>(p => p.Add(x => x.Value, 90));
        cut.Find("input").Change("90"); // no colon ⇒ rejected
        cut.Find("input").GetAttribute("value").Should().Be("01:30", "the field reverts to the last good value");
    }

    [Fact]
    public void Clearing_RaisesNull()
    {
        int? captured = 1;
        var cut = RenderComponent<DurationInput>(p => p
            .Add(x => x.Value, 90)
            .Add(x => x.ValueChanged, (int? v) => captured = v));

        cut.Find("input").Change("");
        captured.Should().BeNull();
    }

    [Fact]
    public void StepUp_AddsStep_StepDown_SubtractsStep()
    {
        int? captured = null;
        var cut = RenderComponent<DurationInput>(p => p
            .Add(x => x.Value, 45)
            .Add(x => x.Step, 5)
            .Add(x => x.ValueChanged, (int? v) => captured = v));

        cut.Find($"button[aria-label=\"{Up}\"]").Click();
        captured.Should().Be(50);

        cut.Find($"button[aria-label=\"{Down}\"]").Click();
        captured.Should().Be(40);
    }

    [Fact]
    public void ShiftClick_UsesShiftStep()
    {
        int? captured = null;
        var cut = RenderComponent<DurationInput>(p => p
            .Add(x => x.Value, 45)
            .Add(x => x.ShiftStep, 60)
            .Add(x => x.ValueChanged, (int? v) => captured = v));

        cut.Find($"button[aria-label=\"{Up}\"]").Click(new MouseEventArgs { ShiftKey = true });
        captured.Should().Be(105, "Shift+▲ adds one hour");
    }

    [Fact]
    public void Arrow_Keys_Step_Like_Buttons()
    {
        int? captured = null;
        var cut = RenderComponent<DurationInput>(p => p
            .Add(x => x.Value, 45)
            .Add(x => x.ValueChanged, (int? v) => captured = v));

        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "ArrowUp" });
        captured.Should().Be(50);

        cut.Find("input").KeyDown(new KeyboardEventArgs { Key = "ArrowDown", ShiftKey = true });
        captured.Should().Be(45 - 60 < 0 ? 0 : 45 - 60); // floors at 0
        captured.Should().Be(0);
    }

    [Fact]
    public void StepDown_FloorsAtZero()
    {
        int? captured = null;
        var cut = RenderComponent<DurationInput>(p => p
            .Add(x => x.Value, 0)
            .Add(x => x.ValueChanged, (int? v) => captured = v));

        cut.Find($"button[aria-label=\"{Down}\"]").Click();
        captured.Should().Be(0);
    }

    [Fact]
    public void StepUp_ClampsToMax()
    {
        int? captured = null;
        var cut = RenderComponent<DurationInput>(p => p
            .Add(x => x.Value, 58)
            .Add(x => x.Step, 5)
            .Add(x => x.Max, 60)
            .Add(x => x.ValueChanged, (int? v) => captured = v));

        cut.Find($"button[aria-label=\"{Up}\"]").Click();
        captured.Should().Be(60, "the stepper never exceeds Max");
    }
}
