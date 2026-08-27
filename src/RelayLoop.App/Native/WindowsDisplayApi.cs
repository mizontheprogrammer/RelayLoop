using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RelayLoop.App.Native;

public interface IDisplayNativeFacade
{
    VirtualDesktopBounds GetVirtualDesktopBounds();

    IReadOnlyList<NativeMonitor> EnumerateMonitors();
}

public sealed class WindowsDisplayApi : IDisplayNativeFacade
{
    public VirtualDesktopBounds GetVirtualDesktopBounds() => new(
        NativeMethods.GetSystemMetrics(NativeConstants.SmXVirtualScreen),
        NativeMethods.GetSystemMetrics(NativeConstants.SmYVirtualScreen),
        NativeMethods.GetSystemMetrics(NativeConstants.SmCxVirtualScreen),
        NativeMethods.GetSystemMetrics(NativeConstants.SmCyVirtualScreen));

    public IReadOnlyList<NativeMonitor> EnumerateMonitors()
    {
        var monitors = new List<NativeMonitor>();
        MonitorEnumerationCallback callback = (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx
            {
                Size = unchecked((uint)Marshal.SizeOf<MonitorInfoEx>()),
            };

            if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetMonitorInfo failed.");
            }

            var (dpiX, dpiY) = GetDpi(monitor);
            monitors.Add(new NativeMonitor(
                monitor,
                info.DeviceName ?? string.Empty,
                info.Monitor.Left,
                info.Monitor.Top,
                info.Monitor.Width,
                info.Monitor.Height,
                info.Work.Left,
                info.Work.Top,
                info.Work.Width,
                info.Work.Height,
                dpiX,
                dpiY,
                (info.Flags & NativeConstants.MonitorInfoFPrimary) != 0));
            return true;
        };

        if (!NativeMethods.EnumDisplayMonitors(0, 0, callback, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "EnumDisplayMonitors failed.");
        }

        return monitors;
    }

    private static (uint DpiX, uint DpiY) GetDpi(nint monitor)
    {
        try
        {
            var result = NativeMethods.GetDpiForMonitor(monitor, MonitorDpiType.Effective, out var dpiX, out var dpiY);
            return result >= 0 ? (dpiX, dpiY) : (96, 96);
        }
        catch (DllNotFoundException)
        {
            return (96, 96);
        }
        catch (EntryPointNotFoundException)
        {
            return (96, 96);
        }
    }

    private delegate bool MonitorEnumerationCallback(nint monitor, nint deviceContext, nint rectangle, nint data);

    private enum MonitorDpiType
    {
        Effective = 0,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;

        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public NativeRectangle Monitor;
        public NativeRectangle Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string? DeviceName;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayMonitors(
            nint hdc,
            nint clip,
            MonitorEnumerationCallback callback,
            nint data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int index);

        [DllImport("shcore.dll")]
        internal static extern int GetDpiForMonitor(
            nint monitor,
            MonitorDpiType dpiType,
            out uint dpiX,
            out uint dpiY);
    }
}
