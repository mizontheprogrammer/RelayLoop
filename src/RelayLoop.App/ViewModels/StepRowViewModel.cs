using RelayLoop.App.Mvvm;
using RelayLoop.Core;

namespace RelayLoop.App.ViewModels;

public sealed class StepRowViewModel : ObservableObject
{
    private MacroStepDefinition _model;
    private int _index;
    private bool _isCapturing;
    private readonly Action<StepRowViewModel> _changed;

    public StepRowViewModel(MacroStepDefinition model, int index, Action<StepRowViewModel> changed)
    { _model = model.DeepClone(); _index = index; _changed = changed; }

    public int Index { get => _index; set => SetProperty(ref _index, value); }
    public IReadOnlyList<MacroStepAction> ActionChoices { get; } = Enum.GetValues<MacroStepAction>();
    public IReadOnlyList<DurationUnit> UnitChoices { get; } = Enum.GetValues<DurationUnit>();
    public MacroStepAction Action { get => _model.Action; set { if (_model.Action == value) return; _model.Action = value; if (value == MacroStepAction.Wait) _model.Inputs.Clear(); Changed(); } }
    public double Duration { get => _model.Duration; set { if (_model.Duration == value) return; _model.Duration = value; Changed(); } }
    public DurationUnit DurationUnit { get => _model.DurationUnit; set { if (_model.DurationUnit == value) return; _model.DurationUnit = value; Changed(); } }
    public double DelayAfter { get => _model.DelayAfter; set { if (_model.DelayAfter == value) return; _model.DelayAfter = value; Changed(); } }
    public DurationUnit DelayUnit { get => _model.DelayUnit; set { if (_model.DelayUnit == value) return; _model.DelayUnit = value; Changed(); } }
    public int MouseX { get => _model.MouseX; set { if (_model.MouseX == value) return; _model.MouseX = value; Changed(); } }
    public int MouseY { get => _model.MouseY; set { if (_model.MouseY == value) return; _model.MouseY = value; Changed(); } }
    public bool IsCapturing { get => _isCapturing; set { if (SetProperty(ref _isCapturing, value)) OnPropertyChanged(nameof(InputSummary)); } }
    public string InputSummary => IsCapturing ? "Press a key or combination." : InputNameFormatter.Format(_model.Inputs);
    public MacroStepDefinition ToModel() => _model.DeepClone();
    public void ReplaceInputs(IEnumerable<MacroInputDefinition> inputs) { _model.Inputs = inputs.Select(static input => input.DeepClone()).ToList(); if (_model.Action == MacroStepAction.Wait) _model.Action = MacroStepAction.Hold; Changed(); }
    public void ClearInputs() { _model.Inputs.Clear(); Changed(); }
    private void Changed() { OnPropertyChanged(string.Empty); _changed(this); }
}

public static class InputNameFormatter
{
    public static string Format(IEnumerable<MacroInputDefinition> inputs)
    {
        var names = inputs.Select(Format).ToArray();
        return names.Length == 0 ? "Not assigned" : string.Join(" + ", names);
    }

    public static string Format(MacroInputDefinition input)
    {
        if (input.Kind == MacroInputKind.MouseButton) return input.Button switch
        { MouseButton.Left => "Mouse Button 1", MouseButton.Right => "Mouse Button 2", MouseButton.Middle => "Middle Mouse", MouseButton.X1 => "Mouse Button 4", MouseButton.X2 => "Mouse Button 5", _ => "Mouse" };
        var vk = input.VirtualKey;
        if (vk is >= 0x41 and <= 0x5A || vk is >= 0x30 and <= 0x39) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x7B) return $"F{vk - 0x6F}";
        if (vk is >= 0x60 and <= 0x69) return $"Numpad {vk - 0x60}";
        return Names.TryGetValue(vk, out var name) ? name : $"Key 0x{vk:X2}";
    }

    private static readonly IReadOnlyDictionary<int, string> Names = new Dictionary<int, string>
    {
        [0x08]="Backspace", [0x09]="Tab", [0x0D]="Enter", [0x10]="Shift", [0x11]="Ctrl", [0x12]="Alt", [0x1B]="Escape", [0x20]="Space",
        [0x21]="Page Up", [0x22]="Page Down", [0x23]="End", [0x24]="Home", [0x25]="Left", [0x26]="Up", [0x27]="Right", [0x28]="Down",
        [0x2D]="Insert", [0x2E]="Delete", [0x5B]="Left Windows", [0x5C]="Right Windows", [0x6A]="Numpad *", [0x6B]="Numpad +", [0x6D]="Numpad -", [0x6E]="Numpad .", [0x6F]="Numpad /",
        [0xBA]=";", [0xBB]="=", [0xBC]=",", [0xBD]="-", [0xBE]=".", [0xBF]="/", [0xC0]="`", [0xDB]="[", [0xDC]="\\", [0xDD]="]", [0xDE]="'",
    };
}
