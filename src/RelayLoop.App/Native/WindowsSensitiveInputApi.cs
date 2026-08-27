using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace RelayLoop.App.Native;

public interface ISensitiveInputNativeFacade
{
    bool IsCurrentInputDesktop();

    nint GetFocusedWindow();

    bool IsStandardPasswordEdit(nint window);
}

/// <summary>Fast, non-content Win32 checks used by the sensitive-input guard.</summary>
public sealed class WindowsSensitiveInputApi : ISensitiveInputNativeFacade
{
    private const uint DesktopReadObjects = 0x0001;
    private const int UserObjectName = 2;
    private const int GuiThreadInfoSize = 72;
    private const int WindowStyleIndex = -16;
    private const long EditPasswordStyle = 0x0020;

    public bool IsCurrentInputDesktop()
    {
        var inputDesktop = NativeMethods.OpenInputDesktop(0, false, DesktopReadObjects);
        if (inputDesktop == 0)
        {
            return false;
        }

        try
        {
            var threadDesktop = NativeMethods.GetThreadDesktop(NativeMethods.GetCurrentThreadId());
            if (threadDesktop == 0)
            {
                return false;
            }

            var inputName = GetDesktopName(inputDesktop);
            var threadName = GetDesktopName(threadDesktop);
            return inputName is not null &&
                   threadName is not null &&
                   string.Equals(inputName, threadName, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            NativeMethods.CloseDesktop(inputDesktop);
        }
    }

    public nint GetFocusedWindow()
    {
        var info = new GuiThreadInfo
        {
            Size = GuiThreadInfoSize,
        };
        if (!NativeMethods.GetGUIThreadInfo(0, ref info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The focused window could not be inspected.");
        }

        return info.FocusWindow;
    }

    public bool IsStandardPasswordEdit(nint window)
    {
        if (window == 0)
        {
            return false;
        }

        var className = new StringBuilder(64);
        if (NativeMethods.GetClassName(window, className, className.Capacity) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The focused control class could not be inspected.");
        }

        if (!string.Equals(className.ToString(), "Edit", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Marshal.SetLastPInvokeError(0);
        var style = NativeMethods.GetWindowLongPtr(window, WindowStyleIndex).ToInt64();
        var error = Marshal.GetLastPInvokeError();
        if (style == 0 && error != 0)
        {
            throw new Win32Exception(error, "The focused control style could not be inspected.");
        }

        return (style & EditPasswordStyle) != 0;
    }

    private static string? GetDesktopName(nint desktop)
    {
        var buffer = new StringBuilder(256);
        return NativeMethods.GetUserObjectInformation(
            desktop,
            UserObjectName,
            buffer,
            unchecked((uint)(buffer.Capacity * sizeof(char))),
            out _)
            ? buffer.ToString()
            : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public nint ActiveWindow;
        public nint FocusWindow;
        public nint CaptureWindow;
        public nint MenuOwnerWindow;
        public nint MoveSizeWindow;
        public nint CaretWindow;
        public NativeRectangle CaretRectangle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseDesktop(nint desktop);

        [DllImport("user32.dll")]
        internal static extern nint GetThreadDesktop(uint threadId);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetUserObjectInformation(
            nint handle,
            int index,
            StringBuilder information,
            uint length,
            out uint needed);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(nint window, StringBuilder className, int maximumCount);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        internal static extern nint GetWindowLongPtr(nint window, int index);
    }
}
