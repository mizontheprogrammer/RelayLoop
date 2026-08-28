namespace RelayLoop.Core;

public static class MacroStepCompiler
{
    public const int MaxStepCount = 10_000;
    public const int MaxInputsPerStep = 32;

    public static List<MacroEvent> Compile(IEnumerable<MacroStepDefinition> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var snapshot = steps.Select(static step => step?.DeepClone() ?? throw new ArgumentException("Steps cannot contain null values.")).ToList();
        if (snapshot.Count > MaxStepCount) throw new ArgumentException($"No more than {MaxStepCount:N0} steps are allowed.");
        var result = new List<MacroEvent>();
        foreach (var step in snapshot)
        {
            Validate(step);
            var inputs = step.Inputs;
            if (step.Action == MacroStepAction.Wait)
            {
                result.Add(CreateDelay(ToMicroseconds(step.Duration, step.DurationUnit)));
            }
            else if (step.Action == MacroStepAction.Release)
            {
                foreach (var input in inputs.AsEnumerable().Reverse()) result.Add(CreateEvent(input, false, step));
            }
            else
            {
                foreach (var input in inputs) result.Add(CreateEvent(input, true, step));
                if (step.Action == MacroStepAction.Hold)
                {
                    var firstRelease = true;
                    foreach (var input in inputs.AsEnumerable().Reverse())
                    {
                        var item = CreateEvent(input, false, step);
                        if (firstRelease) { item.DelayMicroseconds = ToMicroseconds(step.Duration, step.DurationUnit); firstRelease = false; }
                        result.Add(item);
                    }
                }
                else
                {
                    foreach (var input in inputs.AsEnumerable().Reverse()) result.Add(CreateEvent(input, false, step));
                }
            }

            if (step.DelayAfter > 0) result.Add(CreateDelay(ToMicroseconds(step.DelayAfter, step.DelayUnit)));
        }
        return result;
    }

    public static List<MacroStepDefinition> CreateDefault(int mouseX = 0, int mouseY = 0) =>
    [
        CreateHold(0x44, mouseX, mouseY),
        CreateHold(0x41, mouseX, mouseY),
    ];

    private static MacroStepDefinition CreateHold(int virtualKey, int x, int y) => new()
    {
        Action = MacroStepAction.Hold, Duration = 2, DurationUnit = DurationUnit.Minutes,
        MouseX = x, MouseY = y,
        Inputs =
        [
            new MacroInputDefinition { Kind = MacroInputKind.Keyboard, VirtualKey = virtualKey },
            new MacroInputDefinition { Kind = MacroInputKind.MouseButton, Button = MouseButton.Left },
        ],
    };

    public static long ToMicroseconds(double value, DurationUnit unit)
    {
        if (!double.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Duration must be a finite non-negative number.");
        var multiplier = unit switch
        {
            DurationUnit.Milliseconds => 1_000d,
            DurationUnit.Seconds => 1_000_000d,
            DurationUnit.Minutes => 60_000_000d,
            _ => throw new ArgumentOutOfRangeException(nameof(unit)),
        };
        var result = value * multiplier;
        if (result > MacroValidator.MaxDelayMicroseconds) throw new ArgumentOutOfRangeException(nameof(value), "Duration cannot exceed seven days.");
        return checked((long)Math.Round(result, MidpointRounding.AwayFromZero));
    }

    private static void Validate(MacroStepDefinition step)
    {
        if (!Enum.IsDefined(step.Action) || !Enum.IsDefined(step.DurationUnit) || !Enum.IsDefined(step.DelayUnit)) throw new ArgumentException("A step contains an unknown action or duration unit.");
        if (step.Inputs is null) throw new ArgumentException("Step inputs are required.");
        if (step.Inputs.Count > MaxInputsPerStep) throw new ArgumentException($"A step can contain at most {MaxInputsPerStep} inputs.");
        if (step.Action != MacroStepAction.Wait && step.Inputs.Count == 0) throw new ArgumentException("This action requires at least one input.");
        if (step.Action == MacroStepAction.Wait && step.Inputs.Count != 0) throw new ArgumentException("A wait step cannot contain inputs.");
        _ = ToMicroseconds(step.Duration, step.DurationUnit);
        _ = ToMicroseconds(step.DelayAfter, step.DelayUnit);
        foreach (var input in step.Inputs)
        {
            if (input is null) throw new ArgumentException("Step inputs cannot contain null values.");
            if (input.Kind == MacroInputKind.Keyboard && input.VirtualKey is < 1 or > MacroValidator.MaxVirtualKey) throw new ArgumentException("Keyboard virtual keys must be between 1 and 255.");
            if (input.Kind == MacroInputKind.MouseButton && (input.Button == MouseButton.None || !Enum.IsDefined(input.Button))) throw new ArgumentException("A supported mouse button is required.");
            if (!Enum.IsDefined(input.Kind)) throw new ArgumentException("Unknown input kind.");
        }
    }

    private static MacroEvent CreateEvent(MacroInputDefinition input, bool down, MacroStepDefinition step) => input.Kind switch
    {
        MacroInputKind.Keyboard => new MacroEvent { Kind = down ? MacroEventKind.KeyDown : MacroEventKind.KeyUp, VirtualKey = input.VirtualKey, ScanCode = input.ScanCode, IsExtendedKey = input.IsExtendedKey },
        MacroInputKind.MouseButton => new MacroEvent { Kind = down ? MacroEventKind.MouseButtonDown : MacroEventKind.MouseButtonUp, Button = input.Button, X = step.MouseX, Y = step.MouseY },
        _ => throw new ArgumentOutOfRangeException(nameof(input)),
    };

    private static MacroEvent CreateDelay(long microseconds) => new() { Kind = MacroEventKind.MouseMove, DelayMicroseconds = microseconds, Enabled = false };
}
