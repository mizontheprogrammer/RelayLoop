using RelayLoop.App.Native;
using RelayLoop.Core;

namespace RelayLoop.App.Services;

public interface IInputRecorderService : IDisposable
{
    event EventHandler<MacroEventRecordedEventArgs>? EventRecorded;

    event EventHandler? EmergencyStopRequested;

    event EventHandler<Exception>? Faulted;

    event EventHandler<SensitiveInputBlockedEventArgs>? SensitiveInputBlocked;

    bool IsRecording { get; }

    IReadOnlyList<MacroEvent> Snapshot { get; }

    void Start();

    IReadOnlyList<MacroEvent> Stop();

    void ConfigureControlGestures(
        HotKeyGesture recordGesture,
        HotKeyGesture playGesture,
        HotKeyGesture pauseGesture,
        HotKeyGesture emergencyStopGesture);
}

public sealed class MacroEventRecordedEventArgs(MacroEvent macroEvent, int eventCount) : EventArgs
{
    public MacroEvent MacroEvent { get; } = macroEvent;

    public int EventCount { get; } = eventCount;
}

public sealed class RecordingLimitExceededException(int maximumEventCount) : InvalidOperationException(
    $"Recording stopped after reaching the safe limit of {maximumEventCount:N0} events.")
{
    public int MaximumEventCount { get; } = maximumEventCount;
}

/// <summary>
/// Converts low-level hook messages into portable macro events. All mutable state is protected
/// because callbacks arrive on the hook thread while Start/Stop normally arrive on the UI thread.
/// </summary>
public sealed class InputRecorderService : IInputRecorderService
{
    private const uint VirtualKeyControl = 0x11;
    private const uint VirtualKeyShift = 0x10;
    private const uint VirtualKeyMenu = 0x12;
    private const uint VirtualKeyLeftShift = 0xA0;
    private const uint VirtualKeyRightShift = 0xA1;
    private const uint VirtualKeyLeftControl = 0xA2;
    private const uint VirtualKeyRightControl = 0xA3;
    private const uint VirtualKeyLeftMenu = 0xA4;
    private const uint VirtualKeyRightMenu = 0xA5;
    private const uint VirtualKeyLeftWindows = 0x5B;
    private const uint VirtualKeyRightWindows = 0x5C;

    private readonly ILowLevelInputSource _source;
    private readonly IMonotonicClock _clock;
    private readonly ISensitiveInputGuard _sensitiveInputGuard;
    private readonly bool _ownsSensitiveInputGuard;
    private readonly object _gate = new();
    private readonly List<MacroEvent> _events = [];
    private readonly List<PendingKeyboardEvent> _pendingControlModifiers = [];
    private readonly HashSet<uint> _pressedKeys = [];
    private readonly HashSet<uint> _suppressedChordKeys = [];
    private readonly HashSet<uint> _consumedControlPrimaryKeys = [];
    private readonly int _maximumEventCount;
    private HotKeyGesture _recordGesture;
    private HotKeyGesture _playGesture;
    private HotKeyGesture _pauseGesture;
    private HotKeyGesture _emergencyStopGesture;
    private bool _recording;
    private bool _disposed;
    private long _lastRecordedTimestamp;
    private SensitiveInputBlockReason _lastReportedBlockReason;

    public InputRecorderService(
        ILowLevelInputSource source,
        IMonotonicClock? clock = null,
        HotKeyGesture? emergencyStopGesture = null,
        ISensitiveInputGuard? sensitiveInputGuard = null,
        int maximumEventCount = MacroValidator.MaxEventCount)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _clock = clock ?? new StopwatchMonotonicClock();
        _sensitiveInputGuard = sensitiveInputGuard ?? new WindowsSensitiveInputGuard();
        _ownsSensitiveInputGuard = sensitiveInputGuard is null;
        if (maximumEventCount is < 1 or > MacroValidator.MaxEventCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumEventCount),
                $"The recording limit must be from 1 through {MacroValidator.MaxEventCount:N0} events.");
        }

        _maximumEventCount = maximumEventCount;
        _recordGesture = HotKeyGesture.RecordDefault;
        _playGesture = HotKeyGesture.PlayDefault;
        _pauseGesture = HotKeyGesture.PauseDefault;
        _emergencyStopGesture = emergencyStopGesture ?? HotKeyGesture.EmergencyStopDefault;
        ValidateGesture(_recordGesture);
        ValidateGesture(_playGesture);
        ValidateGesture(_emergencyStopGesture);

        _source.KeyboardInput += OnKeyboardInput;
        _source.MouseInput += OnMouseInput;
        _source.Faulted += OnSourceFaulted;
    }

    public event EventHandler<MacroEventRecordedEventArgs>? EventRecorded;

    public event EventHandler? EmergencyStopRequested;

    public event EventHandler<Exception>? Faulted;

    public event EventHandler<SensitiveInputBlockedEventArgs>? SensitiveInputBlocked;

    public HotKeyGesture EmergencyStopGesture
    {
        get
        {
            lock (_gate)
            {
                return _emergencyStopGesture;
            }
        }
    }

    public bool IsRecording
    {
        get
        {
            lock (_gate)
            {
                return _recording;
            }
        }
    }

    public IReadOnlyList<MacroEvent> Snapshot
    {
        get
        {
            MacroEvent[] references;
            lock (_gate)
            {
                // Keep the hook-thread critical section to a fast reference copy. Allocating the
                // deep clones outside the lock prevents a large recovery snapshot from delaying
                // low-level hook callbacks.
                references = _events.ToArray();
            }

            return references.Select(static item => item.DeepClone()).ToArray();
        }
    }

    public void ConfigureEmergencyStop(HotKeyGesture gesture)
    {
        ValidateGesture(gesture);
        lock (_gate)
        {
            if (_recording)
            {
                throw new InvalidOperationException("The emergency-stop hotkey cannot be changed while recording.");
            }

            _emergencyStopGesture = gesture;
        }
    }

    public void ConfigureControlGestures(
        HotKeyGesture recordGesture,
        HotKeyGesture playGesture,
        HotKeyGesture pauseGesture,
        HotKeyGesture emergencyStopGesture)
    {
        ValidateGesture(recordGesture);
        ValidateGesture(playGesture);
        ValidateGesture(pauseGesture);
        ValidateGesture(emergencyStopGesture);
        lock (_gate)
        {
            if (_recording)
            {
                throw new InvalidOperationException("Control hotkeys cannot be changed while recording.");
            }

            _recordGesture = recordGesture;
            _playGesture = playGesture;
            _pauseGesture = pauseGesture;
            _emergencyStopGesture = emergencyStopGesture;
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_recording)
            {
                throw new InvalidOperationException("Recording is already active.");
            }

            _events.Clear();
            _pendingControlModifiers.Clear();
            _pressedKeys.Clear();
            _suppressedChordKeys.Clear();
            _consumedControlPrimaryKeys.Clear();
            _lastRecordedTimestamp = _clock.GetTimestamp();
            _lastReportedBlockReason = SensitiveInputBlockReason.None;
            _recording = true;
        }

        try
        {
            _source.Start();
        }
        catch
        {
            lock (_gate)
            {
                _recording = false;
            }

            throw;
        }
    }

    public IReadOnlyList<MacroEvent> Stop()
    {
        List<MacroEventRecordedEventArgs> flushed = [];
        Exception? failure = null;
        lock (_gate)
        {
            if (!_recording)
            {
                return _events.Select(static item => item.DeepClone()).ToArray();
            }

            if (HasCapacityLocked(_pendingControlModifiers.Count))
            {
                flushed = FlushPendingModifiersLocked();
                CompleteRecordingLocked();
            }
            else
            {
                failure = StopForLimitLocked();
            }
        }

        RaiseRecorded(flushed);
        _source.Stop();
        if (failure is not null)
        {
            RaiseFaulted(failure);
        }

        return Snapshot;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _source.KeyboardInput -= OnKeyboardInput;
        _source.MouseInput -= OnMouseInput;
        _source.Faulted -= OnSourceFaulted;
        _source.Dispose();
        if (_ownsSensitiveInputGuard)
        {
            _sensitiveInputGuard.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private void OnKeyboardInput(object? sender, KeyboardHookEventArgs args)
    {
        if (args.Input.IsInjected)
        {
            return;
        }

        SensitiveInputState sensitiveState;
        try
        {
            sensitiveState = _sensitiveInputGuard.CurrentState;
        }
        catch
        {
            sensitiveState = new SensitiveInputState(false, SensitiveInputBlockReason.InspectionFailed);
        }

        List<MacroEventRecordedEventArgs>? recorded = null;
        Exception? failure = null;
        var stopRequested = false;
        var shouldStopSource = false;
        var shouldReportSensitiveBlock = false;
        var now = _clock.GetTimestamp();

        lock (_gate)
        {
            if (!_recording)
            {
                return;
            }

            var input = args.Input;
            var isKeyUp = input.IsKeyUp;
            var wasPressed = false;
            if (isKeyUp)
            {
                wasPressed = _pressedKeys.Remove(input.VirtualKey);
            }
            else
            {
                _pressedKeys.Add(input.VirtualKey);
            }

            if (_suppressedChordKeys.Contains(input.VirtualKey))
            {
                if (isKeyUp)
                {
                    _suppressedChordKeys.Remove(input.VirtualKey);
                }

                if (_consumedControlPrimaryKeys.Contains(input.VirtualKey))
                {
                    args.Suppress = true;
                    if (isKeyUp)
                    {
                        _consumedControlPrimaryKeys.Remove(input.VirtualKey);
                    }
                }

                return;
            }

            // Hooks are installed after a recording hotkey is pressed. Ignore release events for
            // keys whose key-down happened before capture began; otherwise the starting hotkey
            // would leave orphaned modifier-up events at the head of every recording.
            if (isKeyUp && !wasPressed)
            {
                return;
            }

            if (!isKeyUp && TryMatchControlGestureLocked(input.VirtualKey, out var controlKind, out var controlGesture))
            {
                args.Suppress = true;
                var unrelatedPendingCount = _pendingControlModifiers.Count(pending =>
                    !IsRequiredModifier(controlGesture.Modifiers, pending.Input.VirtualKey));
                if (!HasCapacityLocked(unrelatedPendingCount))
                {
                    failure = StopForLimitLocked();
                    shouldStopSource = true;
                }
                else
                {
                    recorded = FlushUnrelatedPendingModifiersLocked(controlGesture.Modifiers);
                    _suppressedChordKeys.Add(input.VirtualKey);
                    _consumedControlPrimaryKeys.Add(input.VirtualKey);
                    AddPressedControlModifiersToSuppressedSetLocked(controlGesture.Modifiers);
                    if (controlKind is ControlGestureKind.RecordToggle or ControlGestureKind.EmergencyStop)
                    {
                        CompleteRecordingLocked();
                        stopRequested = true;
                        shouldStopSource = true;
                    }
                }
            }
            else if (!sensitiveState.CanRecordKeyboard)
            {
                DiscardPendingModifiersLocked();
                if (!isKeyUp)
                {
                    _suppressedChordKeys.Add(input.VirtualKey);
                }

                shouldReportSensitiveBlock = MarkSensitiveBlockLocked(sensitiveState.BlockReason);
            }
            else if (IsConfiguredControlModifierLocked(input.VirtualKey))
            {
                _lastReportedBlockReason = SensitiveInputBlockReason.None;
                if (isKeyUp)
                {
                    var requiredCapacity = _pendingControlModifiers.Count + 1;
                    if (HasCapacityLocked(requiredCapacity))
                    {
                        recorded = FlushPendingModifiersLocked();
                        recorded.Add(AddEventLocked(ConvertKeyboard(input), now));
                    }
                    else
                    {
                        failure = StopForLimitLocked();
                        shouldStopSource = true;
                    }
                }
                else
                {
                    if (HasCapacityLocked(_pendingControlModifiers.Count + 1))
                    {
                        _pendingControlModifiers.Add(new PendingKeyboardEvent(input, now));
                    }
                    else
                    {
                        failure = StopForLimitLocked();
                        shouldStopSource = true;
                    }
                }
            }
            else
            {
                _lastReportedBlockReason = SensitiveInputBlockReason.None;
                var requiredCapacity = _pendingControlModifiers.Count + 1;
                if (HasCapacityLocked(requiredCapacity))
                {
                    recorded = FlushPendingModifiersLocked();
                    recorded.Add(AddEventLocked(ConvertKeyboard(input), now));
                }
                else
                {
                    failure = StopForLimitLocked();
                    shouldStopSource = true;
                }
            }
        }

        RaiseRecorded(recorded);
        if (shouldReportSensitiveBlock)
        {
            RaiseSensitiveInputBlocked(sensitiveState.BlockReason);
        }

        if (shouldStopSource)
        {
            _source.Stop();
        }

        if (failure is not null)
        {
            RaiseFaulted(failure);
        }

        if (stopRequested)
        {
            EmergencyStopRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnMouseInput(object? sender, MouseHookEventArgs args)
    {
        if (args.Input.IsInjected)
        {
            return;
        }

        SensitiveInputState sensitiveState;
        try
        {
            sensitiveState = _sensitiveInputGuard.CurrentState;
        }
        catch
        {
            sensitiveState = new SensitiveInputState(false, SensitiveInputBlockReason.InspectionFailed);
        }

        List<MacroEventRecordedEventArgs>? recorded = null;
        Exception? failure = null;
        var shouldStopSource = false;
        var shouldReportSensitiveBlock = false;
        var now = _clock.GetTimestamp();
        lock (_gate)
        {
            if (!_recording)
            {
                return;
            }

            if (!sensitiveState.CanRecordKeyboard)
            {
                DiscardPendingModifiersLocked();
                shouldReportSensitiveBlock = MarkSensitiveBlockLocked(sensitiveState.BlockReason);
                goto Completed;
            }

            _lastReportedBlockReason = SensitiveInputBlockReason.None;
            var macroEvent = ConvertMouse(args.Input);
            var requiredCapacity = _pendingControlModifiers.Count + (macroEvent is null ? 0 : 1);
            if (!HasCapacityLocked(requiredCapacity))
            {
                failure = StopForLimitLocked();
                shouldStopSource = true;
                goto Completed;
            }

            recorded = FlushPendingModifiersLocked();
            if (macroEvent is not null)
            {
                recorded.Add(AddEventLocked(macroEvent, now));
            }

        Completed:;
        }

        RaiseRecorded(recorded);
        if (shouldReportSensitiveBlock)
        {
            RaiseSensitiveInputBlocked(sensitiveState.BlockReason);
        }

        if (shouldStopSource)
        {
            _source.Stop();
        }

        if (failure is not null)
        {
            RaiseFaulted(failure);
        }
    }

    private void OnSourceFaulted(object? sender, Exception exception)
    {
        lock (_gate)
        {
            CompleteRecordingLocked();
        }

        _source.Stop();
        RaiseFaulted(exception);
    }

    private MacroEventRecordedEventArgs AddEventLocked(MacroEvent macroEvent, long timestamp)
    {
        macroEvent.DelayMicroseconds = MonotonicTime.GetElapsedMicroseconds(
            _lastRecordedTimestamp,
            timestamp,
            _clock.Frequency);
        _lastRecordedTimestamp = timestamp;
        _events.Add(macroEvent);
        return new MacroEventRecordedEventArgs(macroEvent.DeepClone(), _events.Count);
    }

    private List<MacroEventRecordedEventArgs> FlushPendingModifiersLocked()
    {
        var recorded = new List<MacroEventRecordedEventArgs>(_pendingControlModifiers.Count + 1);
        foreach (var pending in _pendingControlModifiers)
        {
            recorded.Add(AddEventLocked(ConvertKeyboard(pending.Input), pending.Timestamp));
        }

        _pendingControlModifiers.Clear();
        return recorded;
    }

    private List<MacroEventRecordedEventArgs> FlushUnrelatedPendingModifiersLocked(HotKeyModifiers matchedModifiers)
    {
        var recorded = new List<MacroEventRecordedEventArgs>(_pendingControlModifiers.Count);
        foreach (var pending in _pendingControlModifiers)
        {
            if (IsRequiredModifier(matchedModifiers, pending.Input.VirtualKey))
            {
                _suppressedChordKeys.Add(pending.Input.VirtualKey);
            }
            else
            {
                recorded.Add(AddEventLocked(ConvertKeyboard(pending.Input), pending.Timestamp));
            }
        }

        _pendingControlModifiers.Clear();
        return recorded;
    }

    private void DiscardPendingModifiersLocked()
    {
        foreach (var pending in _pendingControlModifiers)
        {
            _suppressedChordKeys.Add(pending.Input.VirtualKey);
        }

        _pendingControlModifiers.Clear();
    }

    private bool HasCapacityLocked(int additionalEventCount) =>
        additionalEventCount >= 0 && _events.Count <= _maximumEventCount - additionalEventCount;

    private RecordingLimitExceededException StopForLimitLocked()
    {
        var exception = new RecordingLimitExceededException(_maximumEventCount);
        CompleteRecordingLocked();
        return exception;
    }

    private void CompleteRecordingLocked()
    {
        _recording = false;
        _pendingControlModifiers.Clear();
        _pressedKeys.Clear();
        _suppressedChordKeys.Clear();
        _consumedControlPrimaryKeys.Clear();
    }

    private void RaiseRecorded(IEnumerable<MacroEventRecordedEventArgs>? recorded)
    {
        if (recorded is null)
        {
            return;
        }

        foreach (var item in recorded)
        {
            EventRecorded?.Invoke(this, item);
        }
    }

    private void RaiseFaulted(Exception exception)
    {
        try
        {
            Faulted?.Invoke(this, exception);
        }
        catch
        {
            // Fault observers must not keep the native hook installed after a terminal failure.
        }
    }

    private void RaiseSensitiveInputBlocked(SensitiveInputBlockReason reason)
    {
        try
        {
            SensitiveInputBlocked?.Invoke(this, new SensitiveInputBlockedEventArgs(reason));
        }
        catch
        {
            // Status observers must not interrupt the hook callback or resume input capture.
        }
    }

    private bool MarkSensitiveBlockLocked(SensitiveInputBlockReason reason)
    {
        if (reason == _lastReportedBlockReason)
        {
            return false;
        }

        _lastReportedBlockReason = reason;
        return true;
    }

    private bool AreRequiredModifiersPressedLocked(HotKeyModifiers modifiers)
    {
        return (!modifiers.HasFlag(HotKeyModifiers.Control) || IsAnyPressed(VirtualKeyControl, VirtualKeyLeftControl, VirtualKeyRightControl)) &&
               (!modifiers.HasFlag(HotKeyModifiers.Shift) || IsAnyPressed(VirtualKeyShift, VirtualKeyLeftShift, VirtualKeyRightShift)) &&
               (!modifiers.HasFlag(HotKeyModifiers.Alt) || IsAnyPressed(VirtualKeyMenu, VirtualKeyLeftMenu, VirtualKeyRightMenu)) &&
               (!modifiers.HasFlag(HotKeyModifiers.Windows) || IsAnyPressed(VirtualKeyLeftWindows, VirtualKeyRightWindows));
    }

    private bool IsAnyPressed(params uint[] keys) => keys.Any(_pressedKeys.Contains);

    private bool TryMatchControlGestureLocked(
        uint virtualKey,
        out ControlGestureKind kind,
        out HotKeyGesture gesture)
    {
        if (MatchesGestureLocked(_emergencyStopGesture, virtualKey))
        {
            kind = ControlGestureKind.EmergencyStop;
            gesture = _emergencyStopGesture;
            return true;
        }

        if (MatchesGestureLocked(_recordGesture, virtualKey))
        {
            kind = ControlGestureKind.RecordToggle;
            gesture = _recordGesture;
            return true;
        }

        if (MatchesGestureLocked(_playGesture, virtualKey))
        {
            kind = ControlGestureKind.Play;
            gesture = _playGesture;
            return true;
        }

        if (MatchesGestureLocked(_pauseGesture, virtualKey))
        {
            kind = ControlGestureKind.Pause;
            gesture = _pauseGesture;
            return true;
        }

        kind = default;
        gesture = default;
        return false;
    }

    private bool MatchesGestureLocked(HotKeyGesture gesture, uint virtualKey) =>
        gesture.VirtualKey == virtualKey && AreRequiredModifiersPressedLocked(gesture.Modifiers);

    private bool IsConfiguredControlModifierLocked(uint virtualKey) =>
        IsRequiredModifier(_recordGesture.Modifiers, virtualKey) ||
        IsRequiredModifier(_playGesture.Modifiers, virtualKey) ||
        IsRequiredModifier(_pauseGesture.Modifiers, virtualKey) ||
        IsRequiredModifier(_emergencyStopGesture.Modifiers, virtualKey);

    private static bool IsRequiredModifier(HotKeyModifiers modifiers, uint virtualKey) =>
        (modifiers.HasFlag(HotKeyModifiers.Control) && virtualKey is VirtualKeyControl or VirtualKeyLeftControl or VirtualKeyRightControl) ||
        (modifiers.HasFlag(HotKeyModifiers.Shift) && virtualKey is VirtualKeyShift or VirtualKeyLeftShift or VirtualKeyRightShift) ||
        (modifiers.HasFlag(HotKeyModifiers.Alt) && virtualKey is VirtualKeyMenu or VirtualKeyLeftMenu or VirtualKeyRightMenu) ||
        (modifiers.HasFlag(HotKeyModifiers.Windows) && virtualKey is VirtualKeyLeftWindows or VirtualKeyRightWindows);

    private void AddPressedControlModifiersToSuppressedSetLocked(HotKeyModifiers modifiers)
    {
        foreach (var key in _pressedKeys)
        {
            if (IsRequiredModifier(modifiers, key))
            {
                _suppressedChordKeys.Add(key);
            }
        }
    }

    private static MacroEvent ConvertKeyboard(NativeKeyboardEvent input) => new()
    {
        Kind = input.IsKeyUp ? MacroEventKind.KeyUp : MacroEventKind.KeyDown,
        VirtualKey = unchecked((int)input.VirtualKey),
        ScanCode = unchecked((int)input.ScanCode),
        IsExtendedKey = input.Flags.HasFlag(LowLevelKeyboardFlags.Extended),
    };

    private static MacroEvent? ConvertMouse(NativeMouseEvent input)
    {
        var macroEvent = new MacroEvent
        {
            X = input.X,
            Y = input.Y,
        };

        switch (input.Message)
        {
            case MouseWindowMessage.Move:
                macroEvent.Kind = MacroEventKind.MouseMove;
                break;
            case MouseWindowMessage.LeftButtonDown:
                macroEvent.Kind = MacroEventKind.MouseButtonDown;
                macroEvent.Button = MouseButton.Left;
                break;
            case MouseWindowMessage.LeftButtonUp:
                macroEvent.Kind = MacroEventKind.MouseButtonUp;
                macroEvent.Button = MouseButton.Left;
                break;
            case MouseWindowMessage.RightButtonDown:
                macroEvent.Kind = MacroEventKind.MouseButtonDown;
                macroEvent.Button = MouseButton.Right;
                break;
            case MouseWindowMessage.RightButtonUp:
                macroEvent.Kind = MacroEventKind.MouseButtonUp;
                macroEvent.Button = MouseButton.Right;
                break;
            case MouseWindowMessage.MiddleButtonDown:
                macroEvent.Kind = MacroEventKind.MouseButtonDown;
                macroEvent.Button = MouseButton.Middle;
                break;
            case MouseWindowMessage.MiddleButtonUp:
                macroEvent.Kind = MacroEventKind.MouseButtonUp;
                macroEvent.Button = MouseButton.Middle;
                break;
            case MouseWindowMessage.XButtonDown:
                macroEvent.Kind = MacroEventKind.MouseButtonDown;
                macroEvent.Button = input.XButton == 1 ? MouseButton.X1 : MouseButton.X2;
                break;
            case MouseWindowMessage.XButtonUp:
                macroEvent.Kind = MacroEventKind.MouseButtonUp;
                macroEvent.Button = input.XButton == 1 ? MouseButton.X1 : MouseButton.X2;
                break;
            case MouseWindowMessage.Wheel:
            case MouseWindowMessage.HorizontalWheel:
                macroEvent.Kind = MacroEventKind.MouseWheel;
                macroEvent.WheelDelta = input.WheelDelta;
                macroEvent.IsHorizontalWheel = input.Message == MouseWindowMessage.HorizontalWheel;
                break;
            default:
                return null;
        }

        return macroEvent;
    }

    private static void ValidateGesture(HotKeyGesture gesture)
    {
        if (gesture.VirtualKey is 0 or > 0xFF)
        {
            throw new ArgumentOutOfRangeException(nameof(gesture), "A Win32 virtual-key code from 1 through 255 is required.");
        }

        const HotKeyModifiers modifierMask =
            HotKeyModifiers.Alt | HotKeyModifiers.Control | HotKeyModifiers.Shift | HotKeyModifiers.Windows;
        const HotKeyModifiers allowedMask = modifierMask | HotKeyModifiers.NoRepeat;
        if ((gesture.Modifiers & modifierMask) == HotKeyModifiers.None ||
            (gesture.Modifiers & ~allowedMask) != HotKeyModifiers.None)
        {
            throw new ArgumentOutOfRangeException(nameof(gesture), "A supported modifier key is required.");
        }
    }

    private readonly record struct PendingKeyboardEvent(NativeKeyboardEvent Input, long Timestamp);

    private enum ControlGestureKind
    {
        RecordToggle,
        Play,
        Pause,
        EmergencyStop,
    }
}
