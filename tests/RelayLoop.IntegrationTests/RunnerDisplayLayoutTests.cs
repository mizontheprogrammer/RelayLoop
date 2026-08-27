using RelayLoop.Core;
using RelayLoop.Runner;
using Xunit;

namespace RelayLoop.IntegrationTests;

public sealed class RunnerDisplayLayoutTests
{
    [Fact]
    public void Compare_MatchesMonitorsByDeviceName_WhenEnumerationOrderChanges()
    {
        DisplayLayout recorded = CreateLayout();
        DisplayLayout current = recorded.DeepClone();
        current.Monitors.Reverse();

        IReadOnlyList<string> differences = RunnerDisplayLayout.Compare(recorded, current);

        Assert.Empty(differences);
    }

    [Fact]
    public void Compare_DetectsMonitorSwapAndDpiChange_WhenVirtualBoundsStayTheSame()
    {
        DisplayLayout recorded = CreateLayout();
        DisplayLayout current = recorded.DeepClone();
        current.Monitors[0].Left = 1920;
        current.Monitors[0].DpiX = 144;
        current.Monitors[0].DpiY = 144;
        current.Monitors[0].IsPrimary = false;
        current.Monitors[1].Left = 0;
        current.Monitors[1].IsPrimary = true;

        IReadOnlyList<string> differences = RunnerDisplayLayout.Compare(recorded, current);

        Assert.Contains(differences, difference => difference.Contains("bounds changed", StringComparison.Ordinal));
        Assert.Contains(differences, difference => difference.Contains("DPI changed", StringComparison.Ordinal));
        Assert.Contains(differences, difference => difference.Contains("primary-monitor status", StringComparison.Ordinal));
    }

    private static DisplayLayout CreateLayout() => new()
    {
        VirtualLeft = 0,
        VirtualTop = 0,
        VirtualWidth = 3840,
        VirtualHeight = 1080,
        Monitors =
        [
            new MonitorInfo
            {
                DeviceName = @"\\.\DISPLAY1",
                Left = 0,
                Top = 0,
                Width = 1920,
                Height = 1080,
                DpiX = 96,
                DpiY = 96,
                IsPrimary = true,
            },
            new MonitorInfo
            {
                DeviceName = @"\\.\DISPLAY2",
                Left = 1920,
                Top = 0,
                Width = 1920,
                Height = 1080,
                DpiX = 120,
                DpiY = 120,
                IsPrimary = false,
            },
        ],
    };
}
