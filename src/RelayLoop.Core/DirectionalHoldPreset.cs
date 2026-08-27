namespace RelayLoop.Core;

/// <summary>Identifies the active half of the built-in D/A plus left-mouse hold cycle.</summary>
public enum DirectionalHoldPhase
{
    HoldD,
    HoldA,
}

/// <summary>A phase label and its remaining scaled playback time.</summary>
public readonly record struct DirectionalHoldTimer(DirectionalHoldPhase Phase, TimeSpan Remaining);

/// <summary>
/// Builds and recognizes the short repeating preset that holds D and the left mouse button for
/// two minutes, releases them, then does the same with A. Playback repetition is configured by
/// the caller so the serialized macro remains an ordinary, editable RelayLoop document.
/// </summary>
public static class DirectionalHoldPreset
{
    public const int DVirtualKey = 0x44;
    public const int DScanCode = 0x20;
    public const int AVirtualKey = 0x41;
    public const int AScanCode = 0x1E;
    public const long HoldDurationMicroseconds = 120_000_000;

    public static List<MacroEvent> CreateEvents(int cursorX, int cursorY) =>
    [
        CreateKey(MacroEventKind.KeyDown, DVirtualKey, DScanCode, 0),
        CreateLeftButton(MacroEventKind.MouseButtonDown, cursorX, cursorY, 0),
        CreateLeftButton(MacroEventKind.MouseButtonUp, cursorX, cursorY, HoldDurationMicroseconds),
        CreateKey(MacroEventKind.KeyUp, DVirtualKey, DScanCode, 0),
        CreateKey(MacroEventKind.KeyDown, AVirtualKey, AScanCode, 0),
        CreateLeftButton(MacroEventKind.MouseButtonDown, cursorX, cursorY, 0),
        CreateLeftButton(MacroEventKind.MouseButtonUp, cursorX, cursorY, HoldDurationMicroseconds),
        CreateKey(MacroEventKind.KeyUp, AVirtualKey, AScanCode, 0),
    ];

    public static bool IsMatch(IReadOnlyList<MacroEvent>? events)
    {
        if (events is not { Count: 8 })
        {
            return false;
        }

        var x = events[1].X;
        var y = events[1].Y;
        return IsKey(events[0], MacroEventKind.KeyDown, DVirtualKey, DScanCode, 0) &&
               IsLeftButton(events[1], MacroEventKind.MouseButtonDown, x, y, 0) &&
               IsLeftButton(events[2], MacroEventKind.MouseButtonUp, x, y, HoldDurationMicroseconds) &&
               IsKey(events[3], MacroEventKind.KeyUp, DVirtualKey, DScanCode, 0) &&
               IsKey(events[4], MacroEventKind.KeyDown, AVirtualKey, AScanCode, 0) &&
               IsLeftButton(events[5], MacroEventKind.MouseButtonDown, x, y, 0) &&
               IsLeftButton(events[6], MacroEventKind.MouseButtonUp, x, y, HoldDurationMicroseconds) &&
               IsKey(events[7], MacroEventKind.KeyUp, AVirtualKey, AScanCode, 0);
    }

    public static DirectionalHoldTimer GetTimer(TimeSpan elapsed, double speed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }

        if (!double.IsFinite(speed) || speed is < PlaybackOptions.MinimumSpeed or > PlaybackOptions.MaximumSpeed)
        {
            throw new ArgumentOutOfRangeException(nameof(speed));
        }

        var phaseTicks = checked((long)Math.Round(
            TimeSpan.FromMicroseconds(HoldDurationMicroseconds).Ticks / speed,
            MidpointRounding.AwayFromZero));
        var loopTicks = checked(phaseTicks * 2);
        var position = elapsed.Ticks % loopTicks;
        if (position < phaseTicks)
        {
            return new DirectionalHoldTimer(
                DirectionalHoldPhase.HoldD,
                TimeSpan.FromTicks(phaseTicks - position));
        }

        return new DirectionalHoldTimer(
            DirectionalHoldPhase.HoldA,
            TimeSpan.FromTicks(loopTicks - position));
    }

    private static MacroEvent CreateKey(
        MacroEventKind kind,
        int virtualKey,
        int scanCode,
        long delayMicroseconds) => new()
    {
        Kind = kind,
        DelayMicroseconds = delayMicroseconds,
        Enabled = true,
        VirtualKey = virtualKey,
        ScanCode = scanCode,
    };

    private static MacroEvent CreateLeftButton(
        MacroEventKind kind,
        int x,
        int y,
        long delayMicroseconds) => new()
    {
        Kind = kind,
        DelayMicroseconds = delayMicroseconds,
        Enabled = true,
        X = x,
        Y = y,
        Button = MouseButton.Left,
    };

    private static bool IsKey(
        MacroEvent macroEvent,
        MacroEventKind kind,
        int virtualKey,
        int scanCode,
        long delayMicroseconds) =>
        macroEvent.Enabled &&
        macroEvent.Kind == kind &&
        macroEvent.DelayMicroseconds == delayMicroseconds &&
        macroEvent.VirtualKey == virtualKey &&
        macroEvent.ScanCode == scanCode &&
        !macroEvent.IsExtendedKey &&
        macroEvent.Button == MouseButton.None &&
        macroEvent.WheelDelta == 0;

    private static bool IsLeftButton(
        MacroEvent macroEvent,
        MacroEventKind kind,
        int x,
        int y,
        long delayMicroseconds) =>
        macroEvent.Enabled &&
        macroEvent.Kind == kind &&
        macroEvent.DelayMicroseconds == delayMicroseconds &&
        macroEvent.X == x &&
        macroEvent.Y == y &&
        macroEvent.Button == MouseButton.Left &&
        macroEvent.VirtualKey == 0 &&
        macroEvent.ScanCode == 0 &&
        macroEvent.WheelDelta == 0;
}
