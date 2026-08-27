using System.IO;
using RelayLoop.Core;

namespace RelayLoop.Runner;

internal static class RunnerMacroAdapter
{
    internal static RunnerMacroData Create(MacroDocument document, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        List<RunnerInputAction> actions = new(document.Events.Count);
        long elapsedMicroseconds = 0;

        foreach (MacroEvent macroEvent in document.Events)
        {
            if (macroEvent.DelayMicroseconds < 0)
            {
                throw new InvalidDataException("The embedded macro contains a negative event delay.");
            }

            try
            {
                elapsedMicroseconds = checked(elapsedMicroseconds + macroEvent.DelayMicroseconds);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException("The embedded macro duration is too large.", exception);
            }

            if (!macroEvent.Enabled)
            {
                continue;
            }

            actions.Add(CreateAction(macroEvent, FromMicroseconds(elapsedMicroseconds)));
        }

        string name = Path.GetFileNameWithoutExtension(executablePath);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "Exported macro";
        }

        return new RunnerMacroData(name, actions, FromMicroseconds(elapsedMicroseconds));
    }

    private static RunnerInputAction CreateAction(MacroEvent macroEvent, TimeSpan offset)
    {
        RunnerInputActionKind kind = macroEvent.Kind switch
        {
            MacroEventKind.MouseMove => RunnerInputActionKind.MouseMove,
            MacroEventKind.MouseWheel => macroEvent.IsHorizontalWheel
                ? RunnerInputActionKind.HorizontalWheel
                : RunnerInputActionKind.MouseWheel,
            MacroEventKind.KeyDown => RunnerInputActionKind.KeyDown,
            MacroEventKind.KeyUp => RunnerInputActionKind.KeyUp,
            MacroEventKind.MouseButtonDown => MapButton(macroEvent.Button, isDown: true),
            MacroEventKind.MouseButtonUp => MapButton(macroEvent.Button, isDown: false),
            _ => throw new InvalidDataException($"The embedded macro contains an unsupported event kind ({macroEvent.Kind})."),
        };

        if (macroEvent.VirtualKey is < 0 or > ushort.MaxValue ||
            macroEvent.ScanCode is < 0 or > ushort.MaxValue)
        {
            throw new InvalidDataException("The embedded macro contains an invalid keyboard code.");
        }

        return new RunnerInputAction(
            kind,
            offset,
            macroEvent.X,
            macroEvent.Y,
            macroEvent.WheelDelta,
            (ushort)macroEvent.VirtualKey,
            (ushort)macroEvent.ScanCode,
            macroEvent.IsExtendedKey);
    }

    private static RunnerInputActionKind MapButton(MouseButton button, bool isDown) => (button, isDown) switch
    {
        (MouseButton.Left, true) => RunnerInputActionKind.LeftButtonDown,
        (MouseButton.Left, false) => RunnerInputActionKind.LeftButtonUp,
        (MouseButton.Right, true) => RunnerInputActionKind.RightButtonDown,
        (MouseButton.Right, false) => RunnerInputActionKind.RightButtonUp,
        (MouseButton.Middle, true) => RunnerInputActionKind.MiddleButtonDown,
        (MouseButton.Middle, false) => RunnerInputActionKind.MiddleButtonUp,
        (MouseButton.X1, true) => RunnerInputActionKind.X1ButtonDown,
        (MouseButton.X1, false) => RunnerInputActionKind.X1ButtonUp,
        (MouseButton.X2, true) => RunnerInputActionKind.X2ButtonDown,
        (MouseButton.X2, false) => RunnerInputActionKind.X2ButtonUp,
        _ => throw new InvalidDataException("A mouse button event in the embedded macro does not specify a button."),
    };

    private static TimeSpan FromMicroseconds(long microseconds)
    {
        try
        {
            return TimeSpan.FromTicks(checked(microseconds * 10));
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("The embedded macro duration is too large.", exception);
        }
    }
}
