using RelayLoop.App.Native;
using RelayLoop.Core;
using CoreMonitorInfo = RelayLoop.Core.MonitorInfo;

namespace RelayLoop.App.Services;

public sealed record DisplayLayoutDifference(string Property, string RecordedValue, string CurrentValue);

public sealed class DisplayLayoutComparison(IReadOnlyList<DisplayLayoutDifference> differences)
{
    public IReadOnlyList<DisplayLayoutDifference> Differences { get; } = differences;

    public bool IsEquivalent => Differences.Count == 0;

    public bool HasResolutionOrPositionChange => Differences.Any(static difference =>
        difference.Property.Contains("Bounds", StringComparison.Ordinal) ||
        difference.Property.StartsWith("VirtualDesktop", StringComparison.Ordinal));

    public bool HasDpiChange => Differences.Any(static difference =>
        difference.Property.Contains("Dpi", StringComparison.Ordinal));
}

public interface IDisplayLayoutService
{
    DisplayLayout Capture();

    DisplayLayoutComparison Compare(DisplayLayout recorded, DisplayLayout current);

    DisplayLayoutComparison CompareWithCurrent(DisplayLayout recorded);
}

public sealed class DisplayLayoutService : IDisplayLayoutService
{
    private readonly IDisplayNativeFacade _native;

    public DisplayLayoutService(IDisplayNativeFacade? native = null)
    {
        _native = native ?? new WindowsDisplayApi();
    }

    public DisplayLayout Capture()
    {
        var virtualDesktop = _native.GetVirtualDesktopBounds();
        if (!virtualDesktop.IsValid)
        {
            throw new InvalidOperationException("Windows reported an invalid virtual-desktop size.");
        }

        var monitors = _native.EnumerateMonitors();
        if (monitors.Count == 0)
        {
            throw new InvalidOperationException("Windows did not report any active displays.");
        }

        return new DisplayLayout
        {
            VirtualLeft = virtualDesktop.Left,
            VirtualTop = virtualDesktop.Top,
            VirtualWidth = virtualDesktop.Width,
            VirtualHeight = virtualDesktop.Height,
            Monitors = monitors
                .Select(static monitor => new CoreMonitorInfo
                {
                    DeviceName = monitor.DeviceName,
                    Left = monitor.Left,
                    Top = monitor.Top,
                    Width = monitor.Width,
                    Height = monitor.Height,
                    DpiX = monitor.DpiX,
                    DpiY = monitor.DpiY,
                    IsPrimary = monitor.IsPrimary,
                })
                .ToList(),
        };
    }

    public DisplayLayoutComparison CompareWithCurrent(DisplayLayout recorded)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        return Compare(recorded, Capture());
    }

    public DisplayLayoutComparison Compare(DisplayLayout recorded, DisplayLayout current)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(current);

        var differences = new List<DisplayLayoutDifference>();
        CompareValue("VirtualDesktop.Left", recorded.VirtualLeft, current.VirtualLeft, differences);
        CompareValue("VirtualDesktop.Top", recorded.VirtualTop, current.VirtualTop, differences);
        CompareValue("VirtualDesktop.Width", recorded.VirtualWidth, current.VirtualWidth, differences);
        CompareValue("VirtualDesktop.Height", recorded.VirtualHeight, current.VirtualHeight, differences);

        var recordedMonitors = (recorded.Monitors ?? [])
            .OrderBy(static monitor => monitor.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static monitor => monitor.Left)
            .ThenBy(static monitor => monitor.Top)
            .ToArray();
        var currentMonitors = (current.Monitors ?? [])
            .OrderBy(static monitor => monitor.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static monitor => monitor.Left)
            .ThenBy(static monitor => monitor.Top)
            .ToArray();

        CompareValue("MonitorCount", recordedMonitors.Length, currentMonitors.Length, differences);
        var count = Math.Min(recordedMonitors.Length, currentMonitors.Length);
        for (var index = 0; index < count; index++)
        {
            var expected = recordedMonitors[index];
            var actual = currentMonitors[index];
            var prefix = $"Monitor[{index}]";
            CompareValue($"{prefix}.DeviceName", expected.DeviceName, actual.DeviceName, differences);
            CompareValue(
                $"{prefix}.Bounds",
                FormatBounds(expected),
                FormatBounds(actual),
                differences);
            CompareValue($"{prefix}.DpiX", expected.DpiX, actual.DpiX, differences);
            CompareValue($"{prefix}.DpiY", expected.DpiY, actual.DpiY, differences);
            CompareValue($"{prefix}.IsPrimary", expected.IsPrimary, actual.IsPrimary, differences);
        }

        return new DisplayLayoutComparison(differences);
    }

    private static string FormatBounds(CoreMonitorInfo monitor) =>
        $"{monitor.Left},{monitor.Top} {monitor.Width}x{monitor.Height}";

    private static void CompareValue<T>(
        string property,
        T recorded,
        T current,
        ICollection<DisplayLayoutDifference> differences)
    {
        if (!EqualityComparer<T>.Default.Equals(recorded, current))
        {
            differences.Add(new DisplayLayoutDifference(
                property,
                recorded?.ToString() ?? string.Empty,
                current?.ToString() ?? string.Empty));
        }
    }
}
