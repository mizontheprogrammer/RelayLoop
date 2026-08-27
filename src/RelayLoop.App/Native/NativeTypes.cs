using System.Runtime.InteropServices;

namespace RelayLoop.App.Native;

internal static class NativeConstants
{
    public const int WhKeyboardLl = 13;
    public const int WhMouseLl = 14;

    public const uint WmQuit = 0x0012;
    public const uint WmHotKey = 0x0312;
    public const uint WmAppCommand = 0x8001;

    public const int SmXVirtualScreen = 76;
    public const int SmYVirtualScreen = 77;
    public const int SmCxVirtualScreen = 78;
    public const int SmCyVirtualScreen = 79;

    public const uint MonitorInfoFPrimary = 0x00000001;
    public const int MonitorDefaultToNearest = 2;
}

public enum KeyboardWindowMessage : uint
{
    KeyDown = 0x0100,
    KeyUp = 0x0101,
    SystemKeyDown = 0x0104,
    SystemKeyUp = 0x0105,
}

public enum MouseWindowMessage : uint
{
    Move = 0x0200,
    LeftButtonDown = 0x0201,
    LeftButtonUp = 0x0202,
    RightButtonDown = 0x0204,
    RightButtonUp = 0x0205,
    MiddleButtonDown = 0x0207,
    MiddleButtonUp = 0x0208,
    Wheel = 0x020A,
    XButtonDown = 0x020B,
    XButtonUp = 0x020C,
    HorizontalWheel = 0x020E,
}

[Flags]
public enum LowLevelKeyboardFlags : uint
{
    Extended = 0x01,
    LowerIntegrityLevelInjected = 0x02,
    Injected = 0x10,
    AltDown = 0x20,
    Up = 0x80,
}

[Flags]
public enum LowLevelMouseFlags : uint
{
    Injected = 0x00000001,
    LowerIntegrityLevelInjected = 0x00000002,
}

[Flags]
public enum HotKeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
    NoRepeat = 0x4000,
}

[Flags]
public enum NativeMouseInputFlags : uint
{
    Move = 0x0001,
    LeftDown = 0x0002,
    LeftUp = 0x0004,
    RightDown = 0x0008,
    RightUp = 0x0010,
    MiddleDown = 0x0020,
    MiddleUp = 0x0040,
    XDown = 0x0080,
    XUp = 0x0100,
    Wheel = 0x0800,
    HorizontalWheel = 0x1000,
    VirtualDesk = 0x4000,
    Absolute = 0x8000,
}

[Flags]
public enum NativeKeyboardInputFlags : uint
{
    ExtendedKey = 0x0001,
    KeyUp = 0x0002,
    Unicode = 0x0004,
    ScanCode = 0x0008,
}

public readonly record struct NativeKeyboardEvent(
    KeyboardWindowMessage Message,
    uint VirtualKey,
    uint ScanCode,
    LowLevelKeyboardFlags Flags,
    uint Timestamp,
    nuint ExtraInfo)
{
    public bool IsInjected =>
        (Flags & (LowLevelKeyboardFlags.Injected | LowLevelKeyboardFlags.LowerIntegrityLevelInjected)) != 0;

    public bool IsKeyUp => Message is KeyboardWindowMessage.KeyUp or KeyboardWindowMessage.SystemKeyUp;
}

public readonly record struct NativeMouseEvent(
    MouseWindowMessage Message,
    int X,
    int Y,
    uint MouseData,
    LowLevelMouseFlags Flags,
    uint Timestamp,
    nuint ExtraInfo)
{
    public bool IsInjected =>
        (Flags & (LowLevelMouseFlags.Injected | LowLevelMouseFlags.LowerIntegrityLevelInjected)) != 0;

    public short WheelDelta => unchecked((short)(MouseData >> 16));

    public ushort XButton => unchecked((ushort)(MouseData >> 16));
}

public sealed class KeyboardHookEventArgs(NativeKeyboardEvent input) : EventArgs
{
    public NativeKeyboardEvent Input { get; } = input;

    /// <summary>When set by a subscriber, the event is not forwarded to other applications.</summary>
    public bool Suppress { get; set; }
}

public sealed class MouseHookEventArgs(NativeMouseEvent input) : EventArgs
{
    public NativeMouseEvent Input { get; } = input;

    /// <summary>When set by a subscriber, the event is not forwarded to other applications.</summary>
    public bool Suppress { get; set; }
}

public readonly record struct VirtualDesktopBounds(int Left, int Top, int Width, int Height)
{
    public bool IsValid => Width > 0 && Height > 0;
}

public readonly record struct NativeKeyboardInput(
    ushort VirtualKey,
    ushort ScanCode,
    NativeKeyboardInputFlags Flags);

public readonly record struct NativeMouseInput(
    int X,
    int Y,
    uint MouseData,
    NativeMouseInputFlags Flags);

public enum NativeInputKind
{
    Keyboard,
    Mouse,
}

public readonly record struct NativeInputPacket
{
    private NativeInputPacket(
        NativeInputKind kind,
        NativeKeyboardInput keyboard,
        NativeMouseInput mouse)
    {
        Kind = kind;
        Keyboard = keyboard;
        Mouse = mouse;
    }

    public NativeInputKind Kind { get; }

    public NativeKeyboardInput Keyboard { get; }

    public NativeMouseInput Mouse { get; }

    public static NativeInputPacket FromKeyboard(NativeKeyboardInput input) =>
        new(NativeInputKind.Keyboard, input, default);

    public static NativeInputPacket FromMouse(NativeMouseInput input) =>
        new(NativeInputKind.Mouse, default, input);
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeMessage
{
    public nint WindowHandle;
    public uint Message;
    public nuint WParam;
    public nint LParam;
    public uint Time;
    public NativePoint Point;
    public uint Private;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativePoint
{
    public int X;
    public int Y;
}

public readonly record struct NativeMonitor(
    nint Handle,
    string DeviceName,
    int Left,
    int Top,
    int Width,
    int Height,
    int WorkLeft,
    int WorkTop,
    int WorkWidth,
    int WorkHeight,
    uint DpiX,
    uint DpiY,
    bool IsPrimary);
