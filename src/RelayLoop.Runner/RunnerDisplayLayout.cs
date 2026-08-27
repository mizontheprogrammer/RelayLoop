using System.ComponentModel;
using System.Runtime.InteropServices;
using RelayLoop.Core;

namespace RelayLoop.Runner;

internal static class RunnerDisplayLayout
{
    private const uint MonitorInfoPrimary = 0x00000001;
    private const int EffectiveDpi = 0;

    internal static DisplayLayout Capture()
    {
        List<MonitorInfo> monitors = [];
        Exception? callbackFailure = null;
        MonitorEnumProcedure callback = (IntPtr monitor, IntPtr _, ref NativeRect __, IntPtr ___) =>
        {
            try
            {
                MonitorInfoEx info = new()
                {
                    Size = Marshal.SizeOf<MonitorInfoEx>(),
                };
                if (!GetMonitorInfo(monitor, ref info))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "GetMonitorInfo failed.");
                }

                int dpiResult = GetDpiForMonitor(monitor, EffectiveDpi, out uint dpiX, out uint dpiY);
                if (dpiResult < 0)
                {
                    Marshal.ThrowExceptionForHR(dpiResult);
                }

                monitors.Add(new MonitorInfo
                {
                    DeviceName = (info.DeviceName ?? string.Empty).TrimEnd('\0'),
                    Left = info.Monitor.Left,
                    Top = info.Monitor.Top,
                    Width = checked(info.Monitor.Right - info.Monitor.Left),
                    Height = checked(info.Monitor.Bottom - info.Monitor.Top),
                    DpiX = dpiX,
                    DpiY = dpiY,
                    IsPrimary = (info.Flags & MonitorInfoPrimary) != 0,
                });
                return true;
            }
            catch (Exception exception)
            {
                callbackFailure = exception;
                return false;
            }
        };

        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
        {
            if (callbackFailure is not null)
            {
                throw new InvalidOperationException("The current monitor layout could not be inspected.", callbackFailure);
            }

            throw new Win32Exception(Marshal.GetLastWin32Error(), "EnumDisplayMonitors failed.");
        }

        if (monitors.Count == 0)
        {
            throw new InvalidOperationException("Windows did not report any active monitors.");
        }

        return new DisplayLayout
        {
            VirtualLeft = NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen),
            VirtualTop = NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen),
            VirtualWidth = NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen),
            VirtualHeight = NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen),
            Monitors = monitors,
        };
    }

    internal static IReadOnlyList<string> Compare(DisplayLayout recorded, DisplayLayout current)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(current);

        List<string> differences = [];
        AddDifference(differences, "virtual left", recorded.VirtualLeft, current.VirtualLeft);
        AddDifference(differences, "virtual top", recorded.VirtualTop, current.VirtualTop);
        AddDifference(differences, "virtual width", recorded.VirtualWidth, current.VirtualWidth);
        AddDifference(differences, "virtual height", recorded.VirtualHeight, current.VirtualHeight);

        IReadOnlyList<MonitorInfo> recordedMonitors = recorded.Monitors ?? [];
        IReadOnlyList<MonitorInfo> currentMonitors = current.Monitors ?? [];
        if (recordedMonitors.Count != currentMonitors.Count)
        {
            differences.Add($"monitor count changed from {recordedMonitors.Count} to {currentMonitors.Count}");
        }

        HashSet<int> matchedCurrentIndexes = [];
        for (int recordedIndex = 0; recordedIndex < recordedMonitors.Count; recordedIndex++)
        {
            MonitorInfo expected = recordedMonitors[recordedIndex];
            int currentIndex = FindMatchingMonitor(expected, recordedIndex, currentMonitors, matchedCurrentIndexes);
            string label = string.IsNullOrWhiteSpace(expected.DeviceName)
                ? $"monitor {recordedIndex + 1}"
                : expected.DeviceName;
            if (currentIndex < 0)
            {
                differences.Add($"{label} is no longer present");
                continue;
            }

            matchedCurrentIndexes.Add(currentIndex);
            MonitorInfo actual = currentMonitors[currentIndex];
            if (expected.Left != actual.Left || expected.Top != actual.Top ||
                expected.Width != actual.Width || expected.Height != actual.Height)
            {
                differences.Add(
                    $"{label} bounds changed from {FormatBounds(expected)} to {FormatBounds(actual)}");
            }

            if (expected.DpiX != actual.DpiX || expected.DpiY != actual.DpiY)
            {
                differences.Add(
                    $"{label} DPI changed from {expected.DpiX}x{expected.DpiY} to {actual.DpiX}x{actual.DpiY}");
            }

            if (expected.IsPrimary != actual.IsPrimary)
            {
                differences.Add($"{label} primary-monitor status changed");
            }
        }

        for (int currentIndex = 0; currentIndex < currentMonitors.Count; currentIndex++)
        {
            if (!matchedCurrentIndexes.Contains(currentIndex))
            {
                string label = string.IsNullOrWhiteSpace(currentMonitors[currentIndex].DeviceName)
                    ? $"monitor {currentIndex + 1}"
                    : currentMonitors[currentIndex].DeviceName;
                differences.Add($"{label} is newly present");
            }
        }

        return differences;
    }

    private static int FindMatchingMonitor(
        MonitorInfo expected,
        int expectedIndex,
        IReadOnlyList<MonitorInfo> currentMonitors,
        ISet<int> matchedIndexes)
    {
        if (!string.IsNullOrWhiteSpace(expected.DeviceName))
        {
            for (int index = 0; index < currentMonitors.Count; index++)
            {
                if (!matchedIndexes.Contains(index) &&
                    string.Equals(expected.DeviceName, currentMonitors[index].DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return -1;
        }

        if (expectedIndex < currentMonitors.Count && !matchedIndexes.Contains(expectedIndex))
        {
            return expectedIndex;
        }

        for (int index = 0; index < currentMonitors.Count; index++)
        {
            if (!matchedIndexes.Contains(index))
            {
                return index;
            }
        }

        return -1;
    }

    private static void AddDifference(List<string> differences, string label, int expected, int actual)
    {
        if (expected != actual)
        {
            differences.Add($"{label} changed from {expected} to {actual}");
        }
    }

    private static string FormatBounds(MonitorInfo monitor) =>
        $"({monitor.Left},{monitor.Top}) {monitor.Width}x{monitor.Height}";

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool MonitorEnumProcedure(
        IntPtr monitor,
        IntPtr deviceContext,
        ref NativeRect monitorRect,
        IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        internal int Size;
        internal NativeRect Monitor;
        internal NativeRect Work;
        internal uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string? DeviceName;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr deviceContext,
        IntPtr clipRect,
        MonitorEnumProcedure callback,
        IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        IntPtr monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);
}
