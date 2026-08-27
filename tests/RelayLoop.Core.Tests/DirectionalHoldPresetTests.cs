using RelayLoop.Core;

namespace RelayLoop.Core.Tests;

public sealed class DirectionalHoldPresetTests
{
    [Fact]
    public void CreateEvents_BuildsValidBalancedFourMinuteLoop()
    {
        var events = DirectionalHoldPreset.CreateEvents(-1234, 678);
        var document = TestMacros.Create();
        document.Events = events;

        MacroValidator.Validate(document);

        Assert.Equal(8, events.Count);
        Assert.Equal(
            [
                MacroEventKind.KeyDown,
                MacroEventKind.MouseButtonDown,
                MacroEventKind.MouseButtonUp,
                MacroEventKind.KeyUp,
                MacroEventKind.KeyDown,
                MacroEventKind.MouseButtonDown,
                MacroEventKind.MouseButtonUp,
                MacroEventKind.KeyUp,
            ],
            events.Select(static item => item.Kind));
        Assert.Equal(
            [0, 0, 120_000_000, 0, 0, 0, 120_000_000, 0],
            events.Select(static item => item.DelayMicroseconds));
        Assert.Equal((DirectionalHoldPreset.DVirtualKey, DirectionalHoldPreset.DScanCode),
            (events[0].VirtualKey, events[0].ScanCode));
        Assert.Equal((DirectionalHoldPreset.AVirtualKey, DirectionalHoldPreset.AScanCode),
            (events[4].VirtualKey, events[4].ScanCode));
        Assert.All(events.Where(static item => item.Kind is MacroEventKind.MouseButtonDown or MacroEventKind.MouseButtonUp), item =>
        {
            Assert.Equal(MouseButton.Left, item.Button);
            Assert.Equal(-1234, item.X);
            Assert.Equal(678, item.Y);
        });
        Assert.Equal(TimeSpan.FromMinutes(4), PlaybackPlanner.GetSingleLoopDuration(document));
        Assert.True(DirectionalHoldPreset.IsMatch(events));

        var firstEventOfSecondLoop = PlaybackPlanner
            .Enumerate(document, new PlaybackOptions(1, 1, continuous: true))
            .Take(9)
            .Last();
        Assert.Equal(2, firstEventOfSecondLoop.LoopNumber);
        Assert.Equal(MacroEventKind.KeyDown, firstEventOfSecondLoop.Event.Kind);
    }

    [Fact]
    public void IsMatch_RejectsModifiedOrDisabledPreset()
    {
        var events = DirectionalHoldPreset.CreateEvents(10, 20);
        events[2].Enabled = false;
        Assert.False(DirectionalHoldPreset.IsMatch(events));

        events = DirectionalHoldPreset.CreateEvents(10, 20);
        events[6].DelayMicroseconds--;
        Assert.False(DirectionalHoldPreset.IsMatch(events));

        events = DirectionalHoldPreset.CreateEvents(10, 20);
        (events[2], events[3]) = (events[3], events[2]);
        Assert.False(DirectionalHoldPreset.IsMatch(events));
    }

    [Theory]
    [InlineData(0, 1, DirectionalHoldPhase.HoldD, 120)]
    [InlineData(119, 1, DirectionalHoldPhase.HoldD, 1)]
    [InlineData(120, 1, DirectionalHoldPhase.HoldA, 120)]
    [InlineData(239, 1, DirectionalHoldPhase.HoldA, 1)]
    [InlineData(240, 1, DirectionalHoldPhase.HoldD, 120)]
    [InlineData(59, 2, DirectionalHoldPhase.HoldD, 1)]
    [InlineData(60, 2, DirectionalHoldPhase.HoldA, 60)]
    [InlineData(120, 2, DirectionalHoldPhase.HoldD, 60)]
    public void GetTimer_ReportsPhaseAndCountdown(
        int elapsedSeconds,
        double speed,
        DirectionalHoldPhase expectedPhase,
        int expectedRemainingSeconds)
    {
        var timer = DirectionalHoldPreset.GetTimer(TimeSpan.FromSeconds(elapsedSeconds), speed);

        Assert.Equal(expectedPhase, timer.Phase);
        Assert.Equal(TimeSpan.FromSeconds(expectedRemainingSeconds), timer.Remaining);
    }
}
