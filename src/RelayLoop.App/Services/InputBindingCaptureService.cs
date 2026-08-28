using RelayLoop.App.Native;
using RelayLoop.Core;

namespace RelayLoop.App.Services;

public interface IInputBindingCaptureService : IDisposable
{
    bool IsCapturing { get; }
    Task<IReadOnlyList<MacroInputDefinition>> CaptureAsync(CancellationToken cancellationToken = default);
    void Cancel();
}

/// <summary>Captures one physical keyboard/mouse chord without allowing it to reach the foreground app.</summary>
public sealed class InputBindingCaptureService : IInputBindingCaptureService
{
    private readonly ILowLevelInputSource _source;
    private readonly object _gate = new();
    private readonly Dictionary<string, MacroInputDefinition> _captured = [];
    private readonly HashSet<string> _pressed = [];
    private TaskCompletionSource<IReadOnlyList<MacroInputDefinition>>? _completion;
    private CancellationTokenRegistration _cancellationRegistration;
    private bool _disposed;

    public InputBindingCaptureService(ILowLevelInputSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _source.KeyboardInput += OnKeyboardInput;
        _source.MouseInput += OnMouseInput;
        _source.Faulted += OnFaulted;
    }

    public bool IsCapturing { get { lock (_gate) return _completion is not null; } }

    public Task<IReadOnlyList<MacroInputDefinition>> CaptureAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Task<IReadOnlyList<MacroInputDefinition>> task;
        lock (_gate)
        {
            if (_completion is not null) throw new InvalidOperationException("A keybind capture is already active.");
            _captured.Clear();
            _pressed.Clear();
            _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            task = _completion.Task;
            _cancellationRegistration = cancellationToken.Register(Cancel);
        }
        try { _source.Start(); }
        catch (Exception exception) { CompleteWithError(exception); }
        return task;
    }

    public void Cancel() => Complete(cancelled: true);

    private void OnKeyboardInput(object? sender, KeyboardHookEventArgs args)
    {
        if (args.Input.IsInjected || !IsCapturing) return;
        args.Suppress = true;
        var id = $"K:{args.Input.VirtualKey}";
        lock (_gate)
        {
            if (_completion is null) return;
            if (!args.Input.IsKeyUp)
            {
                _pressed.Add(id);
                _captured.TryAdd(id, new MacroInputDefinition
                {
                    Kind = MacroInputKind.Keyboard,
                    VirtualKey = checked((int)args.Input.VirtualKey),
                    ScanCode = checked((int)args.Input.ScanCode),
                    IsExtendedKey = args.Input.Flags.HasFlag(LowLevelKeyboardFlags.Extended),
                });
            }
            else if (_captured.ContainsKey(id)) _pressed.Remove(id);
        }
        TryCompleteChord();
    }

    private void OnMouseInput(object? sender, MouseHookEventArgs args)
    {
        if (args.Input.IsInjected || !TryGetMouseButton(args.Input, out var button, out var isUp)) return;
        args.Suppress = true;
        var id = $"M:{button}";
        lock (_gate)
        {
            if (_completion is null) return;
            if (!isUp)
            {
                _pressed.Add(id);
                _captured.TryAdd(id, new MacroInputDefinition { Kind = MacroInputKind.MouseButton, Button = button });
            }
            else if (_captured.ContainsKey(id)) _pressed.Remove(id);
        }
        TryCompleteChord();
    }

    private void TryCompleteChord()
    {
        lock (_gate)
        {
            if (_completion is null || _captured.Count == 0 || _pressed.Count != 0) return;
        }
        Complete();
    }

    private void Complete(bool cancelled = false)
    {
        TaskCompletionSource<IReadOnlyList<MacroInputDefinition>>? completion;
        IReadOnlyList<MacroInputDefinition> result;
        lock (_gate)
        {
            completion = _completion;
            if (completion is null) return;
            result = _captured.Values.Select(static input => input.DeepClone()).ToArray();
            _completion = null;
            _captured.Clear();
            _pressed.Clear();
            _cancellationRegistration.Dispose();
        }
        _source.Stop();
        if (cancelled) completion.TrySetCanceled(); else completion.TrySetResult(result);
    }

    private void CompleteWithError(Exception exception)
    {
        TaskCompletionSource<IReadOnlyList<MacroInputDefinition>>? completion;
        lock (_gate) { completion = _completion; _completion = null; _cancellationRegistration.Dispose(); }
        _source.Stop();
        completion?.TrySetException(exception);
    }

    private void OnFaulted(object? sender, Exception exception) => CompleteWithError(exception);

    private static bool TryGetMouseButton(NativeMouseEvent input, out MouseButton button, out bool isUp)
    {
        button = input.Message switch
        {
            MouseWindowMessage.LeftButtonDown or MouseWindowMessage.LeftButtonUp => MouseButton.Left,
            MouseWindowMessage.RightButtonDown or MouseWindowMessage.RightButtonUp => MouseButton.Right,
            MouseWindowMessage.MiddleButtonDown or MouseWindowMessage.MiddleButtonUp => MouseButton.Middle,
            MouseWindowMessage.XButtonDown or MouseWindowMessage.XButtonUp => input.XButton == 1 ? MouseButton.X1 : MouseButton.X2,
            _ => MouseButton.None,
        };
        isUp = input.Message is MouseWindowMessage.LeftButtonUp or MouseWindowMessage.RightButtonUp or MouseWindowMessage.MiddleButtonUp or MouseWindowMessage.XButtonUp;
        return button != MouseButton.None;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
        _source.KeyboardInput -= OnKeyboardInput;
        _source.MouseInput -= OnMouseInput;
        _source.Faulted -= OnFaulted;
        _source.Dispose();
    }
}
