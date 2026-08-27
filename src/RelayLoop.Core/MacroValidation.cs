namespace RelayLoop.Core;

public sealed record MacroValidationIssue(string Path, string Message);

public sealed class MacroValidationException : IOException
{
    public MacroValidationException(IReadOnlyList<MacroValidationIssue> issues)
        : base(CreateMessage(issues))
    {
        Issues = issues;
    }

    public IReadOnlyList<MacroValidationIssue> Issues { get; }

    private static string CreateMessage(IReadOnlyList<MacroValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        return issues.Count == 0
            ? "The macro document is invalid."
            : $"The macro document is invalid: {issues[0].Path}: {issues[0].Message}";
    }
}

/// <summary>Defensive bounds for untrusted macro files.</summary>
public static class MacroValidator
{
    public const int MaxEventCount = 1_000_000;
    public const long MaxFileSizeBytes = 128L * 1024 * 1024;
    public const int MaxMonitorCount = 64;
    public const int MaxDeviceNameLength = 256;
    public const int MaxCoordinateMagnitude = 10_000_000;
    public const int MaxDisplayDimension = 10_000_000;
    public const long MaxDelayMicroseconds = 7L * 24 * 60 * 60 * 1_000_000;
    public const int MaxWheelDeltaMagnitude = 120_000;
    public const int MaxVirtualKey = 255;
    public const int MaxScanCode = 65_535;
    public const uint MinDpi = 48;
    public const uint MaxDpi = 960;
    public const int MaxReportedIssues = 256;

    public static void Validate(MacroDocument document)
        => Validate(document, CancellationToken.None);

    public static void Validate(MacroDocument document, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var issues = GetIssuesCore(document, cancellationToken);
        if (issues.Count != 0)
        {
            throw new MacroValidationException(issues);
        }
    }

    public static bool IsValid(MacroDocument? document) => document is not null && GetIssues(document).Count == 0;

    public static IReadOnlyList<MacroValidationIssue> GetIssues(MacroDocument? document) =>
        GetIssuesCore(document, CancellationToken.None);

    private static IReadOnlyList<MacroValidationIssue> GetIssuesCore(
        MacroDocument? document,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var issues = new List<MacroValidationIssue>();
        if (document is null)
        {
            issues.Add(new("$", "Document must not be null."));
            return issues;
        }

        if (!string.Equals(document.Format, MacroDocument.FormatIdentifier, StringComparison.Ordinal))
        {
            issues.Add(new("$.format", $"Expected '{MacroDocument.FormatIdentifier}'."));
        }

        if (document.Version != MacroDocument.CurrentFormatVersion)
        {
            issues.Add(new("$.version", $"Only format version {MacroDocument.CurrentFormatVersion} is supported."));
        }

        if (document.Events is null)
        {
            issues.Add(new("$.events", "Events must be an array."));
        }
        else
        {
            if (document.Events.Count > MaxEventCount)
            {
                issues.Add(new("$.events", $"No more than {MaxEventCount:N0} events are allowed."));
            }

            // Do not spend unbounded CPU or allocate one issue per item after the collection itself
            // has already failed its hard count limit.
            var countToInspect = document.Events.Count > MaxEventCount ? 0 : document.Events.Count;
            for (var index = 0; index < countToInspect && issues.Count < MaxReportedIssues; index++)
            {
                if ((index & 0xFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                ValidateEvent(document.Events[index], index, issues);
            }
        }

        if (document.DisplayLayout is null)
        {
            issues.Add(new("$.displayLayout", "Display layout metadata is required."));
        }
        else
        {
            ValidateDisplayLayout(document.DisplayLayout, issues, cancellationToken);
        }

        return issues;
    }

    private static void ValidateEvent(MacroEvent? macroEvent, int index, List<MacroValidationIssue> issues)
    {
        var path = $"$.events[{index}]";
        if (macroEvent is null)
        {
            issues.Add(new(path, "Event must not be null."));
            return;
        }

        if (!Enum.IsDefined(macroEvent.Kind))
        {
            issues.Add(new($"{path}.kind", "Unknown event kind."));
            return;
        }

        if (macroEvent.DelayMicroseconds is < 0 or > MaxDelayMicroseconds)
        {
            issues.Add(new($"{path}.delayMicroseconds", $"Delay must be between 0 and {MaxDelayMicroseconds}."));
        }

        switch (macroEvent.Kind)
        {
            case MacroEventKind.MouseMove:
                ValidateCoordinates(macroEvent, path, issues);
                break;
            case MacroEventKind.MouseButtonDown:
            case MacroEventKind.MouseButtonUp:
                ValidateCoordinates(macroEvent, path, issues);
                if (macroEvent.Button is MouseButton.None || !Enum.IsDefined(macroEvent.Button))
                {
                    issues.Add(new($"{path}.button", "A known mouse button is required."));
                }

                break;
            case MacroEventKind.MouseWheel:
                ValidateCoordinates(macroEvent, path, issues);
                if (macroEvent.WheelDelta == 0 || Math.Abs((long)macroEvent.WheelDelta) > MaxWheelDeltaMagnitude)
                {
                    issues.Add(new($"{path}.wheelDelta", $"Wheel delta must be non-zero and at most {MaxWheelDeltaMagnitude} in magnitude."));
                }

                break;
            case MacroEventKind.KeyDown:
            case MacroEventKind.KeyUp:
                if (macroEvent.VirtualKey is < 1 or > MaxVirtualKey)
                {
                    issues.Add(new($"{path}.virtualKey", $"Virtual key must be between 1 and {MaxVirtualKey}."));
                }

                if (macroEvent.ScanCode is < 0 or > MaxScanCode)
                {
                    issues.Add(new($"{path}.scanCode", $"Scan code must be between 0 and {MaxScanCode}."));
                }

                break;
        }
    }

    private static void ValidateCoordinates(
        MacroEvent macroEvent,
        string path,
        List<MacroValidationIssue> issues)
    {
        if (!IsCoordinateValid(macroEvent.X))
        {
            issues.Add(new($"{path}.x", $"Coordinate magnitude must not exceed {MaxCoordinateMagnitude}."));
        }

        if (!IsCoordinateValid(macroEvent.Y))
        {
            issues.Add(new($"{path}.y", $"Coordinate magnitude must not exceed {MaxCoordinateMagnitude}."));
        }
    }

    private static void ValidateDisplayLayout(
        DisplayLayout layout,
        List<MacroValidationIssue> issues,
        CancellationToken cancellationToken)
    {
        if (!IsCoordinateValid(layout.VirtualLeft))
        {
            issues.Add(new("$.displayLayout.virtualLeft", "Virtual desktop coordinate is outside the allowed range."));
        }

        if (!IsCoordinateValid(layout.VirtualTop))
        {
            issues.Add(new("$.displayLayout.virtualTop", "Virtual desktop coordinate is outside the allowed range."));
        }

        if (layout.VirtualWidth is < 1 or > MaxDisplayDimension)
        {
            issues.Add(new("$.displayLayout.virtualWidth", $"Width must be between 1 and {MaxDisplayDimension}."));
        }

        if (layout.VirtualHeight is < 1 or > MaxDisplayDimension)
        {
            issues.Add(new("$.displayLayout.virtualHeight", $"Height must be between 1 and {MaxDisplayDimension}."));
        }

        if (layout.Monitors is null)
        {
            issues.Add(new("$.displayLayout.monitors", "Monitors must be an array."));
            return;
        }

        if (layout.Monitors.Count is < 1 or > MaxMonitorCount)
        {
            issues.Add(new("$.displayLayout.monitors", $"A layout must contain between 1 and {MaxMonitorCount} monitors."));
        }

        var primaryCount = 0;
        var countToInspect = Math.Min(layout.Monitors.Count, MaxMonitorCount);
        for (var index = 0; index < countToInspect; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var monitor = layout.Monitors[index];
            var path = $"$.displayLayout.monitors[{index}]";
            if (monitor is null)
            {
                issues.Add(new(path, "Monitor must not be null."));
                continue;
            }

            if (monitor.DeviceName is null || monitor.DeviceName.Length > MaxDeviceNameLength)
            {
                issues.Add(new($"{path}.deviceName", $"Device name must be at most {MaxDeviceNameLength} characters."));
            }

            if (!IsCoordinateValid(monitor.Left) || !IsCoordinateValid(monitor.Top))
            {
                issues.Add(new(path, "Monitor coordinates are outside the allowed range."));
            }

            if (monitor.Width is < 1 or > MaxDisplayDimension || monitor.Height is < 1 or > MaxDisplayDimension)
            {
                issues.Add(new(path, $"Monitor dimensions must be between 1 and {MaxDisplayDimension}."));
            }

            if (monitor.DpiX is < MinDpi or > MaxDpi || monitor.DpiY is < MinDpi or > MaxDpi)
            {
                issues.Add(new(path, $"Monitor DPI must be between {MinDpi} and {MaxDpi}."));
            }

            if (monitor.IsPrimary)
            {
                primaryCount++;
            }
        }

        if (layout.Monitors.Count > 0 && primaryCount != 1)
        {
            issues.Add(new("$.displayLayout.monitors", "Exactly one monitor must be marked primary."));
        }
    }

    private static bool IsCoordinateValid(int value) => value is >= -MaxCoordinateMagnitude and <= MaxCoordinateMagnitude;
}
