using System.Runtime.InteropServices;

namespace RelayLoop.App.Native;

public interface IHotKeyNativeFacade
{
    uint GetCurrentThreadId();

    bool PeekMessage(out NativeMessage message, nint window, uint minimum, uint maximum, uint removeMessage);

    int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum);

    bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

    bool RegisterHotKey(nint window, int id, HotKeyModifiers modifiers, uint virtualKey);

    bool UnregisterHotKey(nint window, int id);

    int GetLastError();
}

public sealed class WindowsHotKeyApi : IHotKeyNativeFacade
{
    public uint GetCurrentThreadId() => NativeMethods.GetCurrentThreadId();

    public bool PeekMessage(out NativeMessage message, nint window, uint minimum, uint maximum, uint removeMessage) =>
        NativeMethods.PeekMessage(out message, window, minimum, maximum, removeMessage);

    public int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum) =>
        NativeMethods.GetMessage(out message, window, minimum, maximum);

    public bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam) =>
        NativeMethods.PostThreadMessage(threadId, message, wParam, lParam);

    public bool RegisterHotKey(nint window, int id, HotKeyModifiers modifiers, uint virtualKey) =>
        NativeMethods.RegisterHotKey(window, id, modifiers, virtualKey);

    public bool UnregisterHotKey(nint window, int id) => NativeMethods.UnregisterHotKey(window, id);

    public int GetLastError() => Marshal.GetLastWin32Error();

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PeekMessage(
            out NativeMessage lpMsg,
            nint hWnd,
            uint wMsgFilterMin,
            uint wMsgFilterMax,
            uint wRemoveMsg);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int GetMessage(
            out NativeMessage lpMsg,
            nint hWnd,
            uint wMsgFilterMin,
            uint wMsgFilterMax);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostThreadMessage(
            uint idThread,
            uint msg,
            nuint wParam,
            nint lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(
            nint hWnd,
            int id,
            HotKeyModifiers fsModifiers,
            uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(nint hWnd, int id);
    }
}
