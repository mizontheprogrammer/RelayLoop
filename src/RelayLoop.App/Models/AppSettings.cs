namespace RelayLoop.App.Models;

public enum ThemePreference
{
    System,
    Light,
    Dark
}

public sealed class HotkeySetting
{
    public uint Modifiers { get; set; }
    public uint VirtualKey { get; set; }
}

public sealed class AppSettings
{
    public const int CurrentVersion = 1;
    public const uint DefaultHotkeyModifiers = 0x0001 | 0x0002 | 0x0004 | 0x4000;

    public int Version { get; set; } = CurrentVersion;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool IsExpanded { get; set; }
    public bool CountdownEnabled { get; set; } = true;
    public double PlaybackSpeed { get; set; } = 1.0;
    public int RepeatCount { get; set; } = 1;
    public bool ContinuousPlayback { get; set; }
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public string? RecentMacroPath { get; set; }
    public HotkeySetting RecordHotkey { get; set; } = new() { Modifiers = DefaultHotkeyModifiers, VirtualKey = 0x52 };
    public HotkeySetting PlayHotkey { get; set; } = new() { Modifiers = DefaultHotkeyModifiers, VirtualKey = 0x50 };
    public HotkeySetting StopHotkey { get; set; } = new() { Modifiers = DefaultHotkeyModifiers, VirtualKey = 0x53 };
}
