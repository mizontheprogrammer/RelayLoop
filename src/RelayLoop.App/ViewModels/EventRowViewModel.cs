using RelayLoop.App.Mvvm;
using RelayLoop.Core;

namespace RelayLoop.App.ViewModels;

public sealed class EventRowViewModel : ObservableObject
{
    private MacroEvent _event;
    private readonly Action<EventRowViewModel, MacroEvent, MacroEvent>? _changed;
    private int _index;

    public EventRowViewModel(MacroEvent macroEvent, int index, Action<EventRowViewModel, MacroEvent, MacroEvent>? changed = null)
    {
        _event = macroEvent ?? throw new ArgumentNullException(nameof(macroEvent));
        _index = index;
        _changed = changed;
    }

    public int Index
    {
        get => _index;
        set => SetProperty(ref _index, value);
    }

    public MacroEventKind Kind
    {
        get => _event.Kind;
        set => Change(macroEvent => macroEvent.Kind = value, nameof(Kind), nameof(Details));
    }

    public bool IsEnabled
    {
        get => _event.Enabled;
        set => Change(macroEvent => macroEvent.Enabled = value, nameof(IsEnabled));
    }

    public double DelayMilliseconds
    {
        get => _event.DelayMicroseconds / 1_000d;
        set
        {
            if (!double.IsFinite(value) || value < 0 || value > MacroValidator.MaxDelayMicroseconds / 1_000d)
            {
                return;
            }

            Change(macroEvent => macroEvent.DelayMicroseconds = checked((long)Math.Round(value * 1_000d)),
                nameof(DelayMilliseconds));
        }
    }

    public int X
    {
        get => _event.X;
        set => Change(macroEvent => macroEvent.X = value, nameof(X), nameof(Details));
    }

    public int Y
    {
        get => _event.Y;
        set => Change(macroEvent => macroEvent.Y = value, nameof(Y), nameof(Details));
    }

    public RelayLoop.Core.MouseButton Button
    {
        get => _event.Button;
        set => Change(macroEvent => macroEvent.Button = value, nameof(Button), nameof(Details));
    }

    public int WheelDelta
    {
        get => _event.WheelDelta;
        set => Change(macroEvent => macroEvent.WheelDelta = value, nameof(WheelDelta), nameof(Details));
    }

    public bool IsHorizontalWheel
    {
        get => _event.IsHorizontalWheel;
        set => Change(macroEvent => macroEvent.IsHorizontalWheel = value, nameof(IsHorizontalWheel), nameof(Details));
    }

    public int VirtualKey
    {
        get => _event.VirtualKey;
        set => Change(macroEvent => macroEvent.VirtualKey = value, nameof(VirtualKey), nameof(Details));
    }

    public int ScanCode
    {
        get => _event.ScanCode;
        set => Change(macroEvent => macroEvent.ScanCode = value, nameof(ScanCode), nameof(Details));
    }

    public bool IsExtendedKey
    {
        get => _event.IsExtendedKey;
        set => Change(macroEvent => macroEvent.IsExtendedKey = value, nameof(IsExtendedKey), nameof(Details));
    }

    public string Details => _event.Kind switch
    {
        MacroEventKind.MouseMove => $"({X}, {Y})",
        MacroEventKind.MouseButtonDown or MacroEventKind.MouseButtonUp => $"{Button} at ({X}, {Y})",
        MacroEventKind.MouseWheel => $"{(WheelDelta >= 0 ? "+" : string.Empty)}{WheelDelta} at ({X}, {Y})",
        MacroEventKind.KeyDown or MacroEventKind.KeyUp => $"VK {VirtualKey} · scan {ScanCode}{(IsExtendedKey ? " · extended" : string.Empty)}",
        _ => string.Empty
    };

    public MacroEvent ToModel() => _event.DeepClone();

    public void Replace(MacroEvent value)
    {
        _event = value.DeepClone();
        OnPropertyChanged(string.Empty);
    }

    private void Change(Action<MacroEvent> mutation, params string[] propertyNames)
    {
        var before = _event.DeepClone();
        mutation(_event);
        var after = _event.DeepClone();
        if (EventsEqual(before, after))
        {
            return;
        }

        foreach (var propertyName in propertyNames)
        {
            OnPropertyChanged(propertyName);
        }

        _changed?.Invoke(this, before, after);
    }

    private static bool EventsEqual(MacroEvent left, MacroEvent right) =>
        left.Kind == right.Kind &&
        left.DelayMicroseconds == right.DelayMicroseconds &&
        left.Enabled == right.Enabled &&
        left.X == right.X && left.Y == right.Y &&
        left.Button == right.Button && left.WheelDelta == right.WheelDelta &&
        left.IsHorizontalWheel == right.IsHorizontalWheel &&
        left.VirtualKey == right.VirtualKey && left.ScanCode == right.ScanCode &&
        left.IsExtendedKey == right.IsExtendedKey;
}
