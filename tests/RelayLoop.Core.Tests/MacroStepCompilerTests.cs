using RelayLoop.Core;

namespace RelayLoop.Core.Tests;

public sealed class MacroStepCompilerTests
{
    [Fact]
    public void HoldChord_PressesTogetherAndReleasesBeforeNextStep()
    {
        var steps = new[]
        {
            new MacroStepDefinition
            {
                Action = MacroStepAction.Hold, Duration = 30, DurationUnit = DurationUnit.Seconds,
                Inputs =
                [
                    new() { Kind = MacroInputKind.Keyboard, VirtualKey = 0x57, ScanCode = 0x11 },
                    new() { Kind = MacroInputKind.Keyboard, VirtualKey = 0x11, ScanCode = 0x1D },
                    new() { Kind = MacroInputKind.MouseButton, Button = MouseButton.Left },
                ],
            },
            new MacroStepDefinition { Action = MacroStepAction.PressOnce, Inputs = [new() { Kind = MacroInputKind.Keyboard, VirtualKey = 0x53 }] },
        };

        var events = MacroStepCompiler.Compile(steps);

        Assert.Equal([MacroEventKind.KeyDown, MacroEventKind.KeyDown, MacroEventKind.MouseButtonDown], events.Take(3).Select(e => e.Kind));
        Assert.All(events.Take(3), e => Assert.Equal(0, e.DelayMicroseconds));
        Assert.Equal(30_000_000, events[3].DelayMicroseconds);
        Assert.Equal([MacroEventKind.MouseButtonUp, MacroEventKind.KeyUp, MacroEventKind.KeyUp], events.Skip(3).Take(3).Select(e => e.Kind));
        Assert.Equal(MacroEventKind.KeyDown, events[6].Kind);
    }

    [Fact]
    public void Default_IsTwoMinuteDAndAWithMouseButtonOne()
    {
        var steps = MacroStepCompiler.CreateDefault(120, 240);
        Assert.Equal(2, steps.Count);
        Assert.Equal([0x44, 0x41], steps.Select(step => step.Inputs[0].VirtualKey));
        Assert.All(steps, step =>
        {
            Assert.Equal(2, step.Duration);
            Assert.Equal(DurationUnit.Minutes, step.DurationUnit);
            Assert.Equal(MouseButton.Left, step.Inputs[1].Button);
        });
    }

    [Fact]
    public void WaitAndDelayUseNonInjectedTimedEvents()
    {
        var events = MacroStepCompiler.Compile([new MacroStepDefinition { Action = MacroStepAction.Wait, Duration = 250, DurationUnit = DurationUnit.Milliseconds, DelayAfter = 1, DelayUnit = DurationUnit.Seconds }]);
        Assert.Equal([250_000L, 1_000_000L], events.Select(e => e.DelayMicroseconds));
        Assert.All(events, e => Assert.False(e.Enabled));
    }
}
