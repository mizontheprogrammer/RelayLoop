using RelayLoop.Core;

namespace RelayLoop.Core.Tests;

internal static class TestMacros
{
    public static MacroDocument Create() => new()
    {
        CreatedUtc = new DateTimeOffset(2026, 8, 25, 12, 30, 0, TimeSpan.Zero),
        DisplayLayout = CreateDisplayLayout(),
        Events =
        [
            new MacroEvent
            {
                Kind = MacroEventKind.MouseMove,
                DelayMicroseconds = 1_000,
                X = -1750,
                Y = 42,
            },
            new MacroEvent
            {
                Kind = MacroEventKind.MouseButtonDown,
                DelayMicroseconds = 2_000,
                X = -1750,
                Y = 42,
                Button = MouseButton.X1,
            },
            new MacroEvent
            {
                Kind = MacroEventKind.MouseButtonUp,
                DelayMicroseconds = 3_000,
                X = -1750,
                Y = 42,
                Button = MouseButton.X1,
            },
            new MacroEvent
            {
                Kind = MacroEventKind.MouseWheel,
                DelayMicroseconds = 4_000,
                X = 100,
                Y = 200,
                WheelDelta = -120,
                IsHorizontalWheel = true,
            },
            new MacroEvent
            {
                Kind = MacroEventKind.KeyDown,
                DelayMicroseconds = 5_000,
                VirtualKey = 0x41,
                ScanCode = 0x1E,
                IsExtendedKey = false,
            },
            new MacroEvent
            {
                Kind = MacroEventKind.KeyUp,
                DelayMicroseconds = 6_000,
                Enabled = false,
                VirtualKey = 0x41,
                ScanCode = 0x1E,
            },
        ],
    };

    public static MacroDocument WithDelays(params long[] delays) => new()
    {
        CreatedUtc = DateTimeOffset.UnixEpoch,
        DisplayLayout = CreateDisplayLayout(),
        Events = delays.Select(delay => new MacroEvent
        {
            Kind = MacroEventKind.KeyDown,
            DelayMicroseconds = delay,
            VirtualKey = 0x41,
            ScanCode = 0x1E,
        }).ToList(),
    };

    private static DisplayLayout CreateDisplayLayout() => new()
    {
        VirtualLeft = -1920,
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
                DpiX = 120,
                DpiY = 120,
                IsPrimary = true,
            },
            new MonitorInfo
            {
                DeviceName = @"\\.\DISPLAY2",
                Left = -1920,
                Top = 0,
                Width = 1920,
                Height = 1080,
                DpiX = 96,
                DpiY = 96,
            },
        ],
    };
}
