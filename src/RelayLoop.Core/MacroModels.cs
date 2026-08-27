using System.Text.Json.Serialization;

namespace RelayLoop.Core;

/// <summary>The input operation represented by a macro event.</summary>
public enum MacroEventKind
{
    MouseMove,
    MouseButtonDown,
    MouseButtonUp,
    MouseWheel,
    KeyDown,
    KeyUp,
}

/// <summary>A mouse button used by a button event.</summary>
public enum MouseButton
{
    None,
    Left,
    Right,
    Middle,
    X1,
    X2,
}

/// <summary>
/// One recorded input event. Coordinates are physical virtual-desktop pixels and may be negative.
/// DelayMicroseconds is the monotonic delay since the preceding recorded event.
/// </summary>
public sealed class MacroEvent
{
    [JsonRequired]
    public MacroEventKind Kind { get; set; }

    [JsonRequired]
    public long DelayMicroseconds { get; set; }

    [JsonRequired]
    public bool Enabled { get; set; } = true;

    [JsonRequired]
    public int X { get; set; }

    [JsonRequired]
    public int Y { get; set; }

    [JsonRequired]
    public MouseButton Button { get; set; }

    [JsonRequired]
    public int WheelDelta { get; set; }

    /// <summary>True for horizontal wheel input; false for vertical wheel input.</summary>
    [JsonRequired]
    public bool IsHorizontalWheel { get; set; }

    [JsonRequired]
    public int VirtualKey { get; set; }

    [JsonRequired]
    public int ScanCode { get; set; }

    [JsonRequired]
    public bool IsExtendedKey { get; set; }

    [JsonIgnore]
    public bool IsEnabled
    {
        get => Enabled;
        set => Enabled = value;
    }

    public MacroEvent DeepClone() => (MacroEvent)MemberwiseClone();
}

/// <summary>A snapshot of the complete Windows virtual desktop when a macro was recorded.</summary>
public sealed class DisplayLayout
{
    [JsonRequired]
    public int VirtualLeft { get; set; }

    [JsonRequired]
    public int VirtualTop { get; set; }

    [JsonRequired]
    public int VirtualWidth { get; set; }

    [JsonRequired]
    public int VirtualHeight { get; set; }

    [JsonRequired]
    public List<MonitorInfo> Monitors { get; set; } = [];

    public DisplayLayout DeepClone() => new()
    {
        VirtualLeft = VirtualLeft,
        VirtualTop = VirtualTop,
        VirtualWidth = VirtualWidth,
        VirtualHeight = VirtualHeight,
        Monitors = Monitors?.Select(static monitor => monitor.DeepClone()).ToList() ?? [],
    };
}

/// <summary>Physical bounds and effective DPI for one display.</summary>
public sealed class MonitorInfo
{
    [JsonRequired]
    public string DeviceName { get; set; } = string.Empty;

    [JsonRequired]
    public int Left { get; set; }

    [JsonRequired]
    public int Top { get; set; }

    [JsonRequired]
    public int Width { get; set; }

    [JsonRequired]
    public int Height { get; set; }

    [JsonRequired]
    public uint DpiX { get; set; } = 96;

    [JsonRequired]
    public uint DpiY { get; set; } = 96;

    [JsonRequired]
    public bool IsPrimary { get; set; }

    public MonitorInfo DeepClone() => (MonitorInfo)MemberwiseClone();
}

/// <summary>The root object stored in a RelayLoop <c>.rloop</c> file.</summary>
public sealed class MacroDocument
{
    public const string FileExtension = ".rloop";
    public const string FormatIdentifier = "RelayLoop.Macro";
    public const int CurrentFormatVersion = 1;

    [JsonRequired]
    public string Format { get; set; } = FormatIdentifier;

    [JsonRequired]
    public int Version { get; set; } = CurrentFormatVersion;

    [JsonRequired]
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonRequired]
    public DisplayLayout? DisplayLayout { get; set; }

    [JsonRequired]
    public List<MacroEvent> Events { get; set; } = [];

    public MacroDocument DeepClone() => new()
    {
        Format = Format,
        Version = Version,
        CreatedUtc = CreatedUtc,
        DisplayLayout = DisplayLayout?.DeepClone(),
        Events = Events?.Select(static macroEvent => macroEvent.DeepClone()).ToList() ?? [],
    };
}
