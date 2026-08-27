using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RelayLoop.App.Native;

public interface ISendInputNativeFacade
{
    VirtualDesktopBounds GetVirtualDesktopBounds();

    uint Send(IReadOnlyList<NativeInputPacket> inputs);
}

public sealed class WindowsSendInputApi : ISendInputNativeFacade
{
    public VirtualDesktopBounds GetVirtualDesktopBounds() => new(
        NativeMethods.GetSystemMetrics(NativeConstants.SmXVirtualScreen),
        NativeMethods.GetSystemMetrics(NativeConstants.SmYVirtualScreen),
        NativeMethods.GetSystemMetrics(NativeConstants.SmCxVirtualScreen),
        NativeMethods.GetSystemMetrics(NativeConstants.SmCyVirtualScreen));

    public uint Send(IReadOnlyList<NativeInputPacket> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
        {
            return 0;
        }

        var nativeInputs = new Input[inputs.Count];
        for (var index = 0; index < inputs.Count; index++)
        {
            nativeInputs[index] = Convert(inputs[index]);
        }

        var sent = NativeMethods.SendInput(
            unchecked((uint)nativeInputs.Length),
            nativeInputs,
            Marshal.SizeOf<Input>());
        if (sent != nativeInputs.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SendInput did not inject every requested input event.");
        }

        return sent;
    }

    private static Input Convert(NativeInputPacket packet) => packet.Kind switch
    {
        NativeInputKind.Keyboard => new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = packet.Keyboard.VirtualKey,
                    ScanCode = packet.Keyboard.ScanCode,
                    Flags = packet.Keyboard.Flags,
                },
            },
        },
        NativeInputKind.Mouse => new Input
        {
            Type = InputMouse,
            Data = new InputUnion
            {
                Mouse = new MouseInput
                {
                    X = packet.Mouse.X,
                    Y = packet.Mouse.Y,
                    MouseData = packet.Mouse.MouseData,
                    Flags = packet.Mouse.Flags,
                },
            },
        },
        _ => throw new ArgumentOutOfRangeException(nameof(packet)),
    };

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public NativeMouseInputFlags Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public NativeKeyboardInputFlags Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(
            uint count,
            [In] Input[] inputs,
            int size);

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int index);
    }
}
