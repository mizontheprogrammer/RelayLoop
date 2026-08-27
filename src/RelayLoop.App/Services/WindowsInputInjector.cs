using RelayLoop.App.Native;
using RelayLoop.Core;

namespace RelayLoop.App.Services;

public readonly record struct HeldKey(int VirtualKey, int ScanCode, bool IsExtendedKey);

public interface IInputInjector
{
    void Inject(MacroEvent macroEvent);

    void ReleaseKey(HeldKey key);

    void ReleaseMouseButton(MouseButton button);
}

/// <summary>Translates validated macro events into SendInput packets.</summary>
public sealed class WindowsInputInjector : IInputInjector
{
    private const uint XButton1 = 0x0001;
    private const uint XButton2 = 0x0002;
    private readonly ISendInputNativeFacade _native;

    public WindowsInputInjector(ISendInputNativeFacade? native = null)
    {
        _native = native ?? new WindowsSendInputApi();
    }

    public void Inject(MacroEvent macroEvent)
    {
        ArgumentNullException.ThrowIfNull(macroEvent);

        var packet = macroEvent.Kind switch
        {
            MacroEventKind.MouseMove => CreateMousePacket(
                macroEvent.X,
                macroEvent.Y,
                0,
                NativeMouseInputFlags.Move),
            MacroEventKind.MouseButtonDown => CreateMousePacket(
                macroEvent.X,
                macroEvent.Y,
                GetMouseData(macroEvent.Button),
                NativeMouseInputFlags.Move | GetButtonFlag(macroEvent.Button, isUp: false)),
            MacroEventKind.MouseButtonUp => CreateMousePacket(
                macroEvent.X,
                macroEvent.Y,
                GetMouseData(macroEvent.Button),
                NativeMouseInputFlags.Move | GetButtonFlag(macroEvent.Button, isUp: true)),
            MacroEventKind.MouseWheel => CreateMousePacket(
                macroEvent.X,
                macroEvent.Y,
                unchecked((uint)macroEvent.WheelDelta),
                NativeMouseInputFlags.Move |
                (macroEvent.IsHorizontalWheel ? NativeMouseInputFlags.HorizontalWheel : NativeMouseInputFlags.Wheel)),
            MacroEventKind.KeyDown => CreateKeyboardPacket(
                new HeldKey(macroEvent.VirtualKey, macroEvent.ScanCode, macroEvent.IsExtendedKey),
                isUp: false),
            MacroEventKind.KeyUp => CreateKeyboardPacket(
                new HeldKey(macroEvent.VirtualKey, macroEvent.ScanCode, macroEvent.IsExtendedKey),
                isUp: true),
            _ => throw new ArgumentOutOfRangeException(nameof(macroEvent), macroEvent.Kind, "Unsupported macro event kind."),
        };

        _native.Send([packet]);
    }

    public void ReleaseKey(HeldKey key) => _native.Send([CreateKeyboardPacket(key, isUp: true)]);

    public void ReleaseMouseButton(MouseButton button)
    {
        if (button == MouseButton.None)
        {
            return;
        }

        _native.Send([
            NativeInputPacket.FromMouse(new NativeMouseInput(
                0,
                0,
                GetMouseData(button),
                GetButtonFlag(button, isUp: true))),
        ]);
    }

    private NativeInputPacket CreateMousePacket(
        int physicalX,
        int physicalY,
        uint mouseData,
        NativeMouseInputFlags flags)
    {
        var bounds = _native.GetVirtualDesktopBounds();
        if (!bounds.IsValid)
        {
            throw new InvalidOperationException("Windows reported an invalid virtual-desktop size.");
        }

        var normalizedX = NormalizeAbsoluteCoordinate(physicalX, bounds.Left, bounds.Width);
        var normalizedY = NormalizeAbsoluteCoordinate(physicalY, bounds.Top, bounds.Height);
        return NativeInputPacket.FromMouse(new NativeMouseInput(
            normalizedX,
            normalizedY,
            mouseData,
            flags | NativeMouseInputFlags.Absolute | NativeMouseInputFlags.VirtualDesk));
    }

    private static NativeInputPacket CreateKeyboardPacket(HeldKey key, bool isUp)
    {
        var flags = isUp ? NativeKeyboardInputFlags.KeyUp : 0;
        if (key.IsExtendedKey)
        {
            flags |= NativeKeyboardInputFlags.ExtendedKey;
        }

        ushort virtualKey;
        ushort scanCode;
        if (key.ScanCode != 0)
        {
            virtualKey = 0;
            scanCode = checked((ushort)key.ScanCode);
            flags |= NativeKeyboardInputFlags.ScanCode;
        }
        else
        {
            virtualKey = checked((ushort)key.VirtualKey);
            scanCode = 0;
        }

        return NativeInputPacket.FromKeyboard(new NativeKeyboardInput(virtualKey, scanCode, flags));
    }

    internal static int NormalizeAbsoluteCoordinate(int coordinate, int origin, int dimension)
    {
        if (dimension <= 1)
        {
            return 0;
        }

        var relative = Math.Clamp((long)coordinate - origin, 0, dimension - 1L);
        return unchecked((int)Math.Round(
            relative * 65_535d / (dimension - 1d),
            MidpointRounding.AwayFromZero));
    }

    private static NativeMouseInputFlags GetButtonFlag(MouseButton button, bool isUp) => (button, isUp) switch
    {
        (MouseButton.Left, false) => NativeMouseInputFlags.LeftDown,
        (MouseButton.Left, true) => NativeMouseInputFlags.LeftUp,
        (MouseButton.Right, false) => NativeMouseInputFlags.RightDown,
        (MouseButton.Right, true) => NativeMouseInputFlags.RightUp,
        (MouseButton.Middle, false) => NativeMouseInputFlags.MiddleDown,
        (MouseButton.Middle, true) => NativeMouseInputFlags.MiddleUp,
        (MouseButton.X1 or MouseButton.X2, false) => NativeMouseInputFlags.XDown,
        (MouseButton.X1 or MouseButton.X2, true) => NativeMouseInputFlags.XUp,
        _ => throw new ArgumentOutOfRangeException(nameof(button), button, "A concrete mouse button is required."),
    };

    private static uint GetMouseData(MouseButton button) => button switch
    {
        MouseButton.X1 => XButton1,
        MouseButton.X2 => XButton2,
        _ => 0,
    };
}
