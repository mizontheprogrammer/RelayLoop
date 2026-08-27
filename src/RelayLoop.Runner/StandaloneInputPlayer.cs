using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace RelayLoop.Runner;

internal enum RunnerInputActionKind
{
    MouseMove,
    LeftButtonDown,
    LeftButtonUp,
    RightButtonDown,
    RightButtonUp,
    MiddleButtonDown,
    MiddleButtonUp,
    X1ButtonDown,
    X1ButtonUp,
    X2ButtonDown,
    X2ButtonUp,
    MouseWheel,
    HorizontalWheel,
    KeyDown,
    KeyUp,
}

internal readonly record struct RunnerInputAction(
    RunnerInputActionKind Kind,
    TimeSpan Offset,
    int X = 0,
    int Y = 0,
    int Data = 0,
    ushort VirtualKey = 0,
    ushort ScanCode = 0,
    bool IsExtendedKey = false);

internal sealed record RunnerMacroData(
    string Name,
    IReadOnlyList<RunnerInputAction> Actions,
    TimeSpan Duration);

internal interface IRunnerInputNativeFacade
{
    int GetSystemMetric(int index);

    void Send(NativeMethods.Input input);
}

internal sealed class WindowsRunnerInputNativeFacade : IRunnerInputNativeFacade
{
    public int GetSystemMetric(int index) => NativeMethods.GetSystemMetrics(index);

    public void Send(NativeMethods.Input input) => NativeMethods.Send(input);
}

internal sealed class RunnerInputReleaseException : Exception
{
    private readonly Exception[] _releaseFailures;

    internal RunnerInputReleaseException(
        int remainingInputCount,
        IEnumerable<Exception> releaseFailures,
        Exception? playbackFailure = null)
        : this(remainingInputCount, releaseFailures.ToArray(), playbackFailure)
    {
    }

    private RunnerInputReleaseException(
        int remainingInputCount,
        Exception[] releaseFailures,
        Exception? playbackFailure)
        : base(
            BuildMessage(remainingInputCount),
            BuildInnerException(releaseFailures, playbackFailure))
    {
        RemainingInputCount = remainingInputCount;
        _releaseFailures = releaseFailures;
    }

    internal int RemainingInputCount { get; }

    internal IReadOnlyList<Exception> ReleaseFailures => _releaseFailures;

    internal RunnerInputReleaseException WithPlaybackFailure(Exception playbackFailure) =>
        new(RemainingInputCount, _releaseFailures, playbackFailure);

    private static string BuildMessage(int remainingInputCount) =>
        $"Windows rejected one or more held-input release events; {remainingInputCount} input(s) may still be held. " +
        "Press and release the affected keys or mouse buttons manually before continuing.";

    private static Exception BuildInnerException(
        IReadOnlyCollection<Exception> releaseFailures,
        Exception? playbackFailure)
    {
        List<Exception> failures = new(releaseFailures.Count + (playbackFailure is null ? 0 : 1));
        if (playbackFailure is not null)
        {
            failures.Add(playbackFailure);
        }

        failures.AddRange(releaseFailures);
        return new AggregateException(failures);
    }
}

internal sealed class StandaloneInputPlayer : IDisposable
{
    private readonly object _gate = new();
    private readonly IRunnerInputNativeFacade _native;
    private readonly HashSet<HeldKey> _heldKeys = [];
    private readonly HashSet<HeldMouseButton> _heldButtons = [];
    private CancellationTokenSource? _playbackCancellation;
    private TaskCompletionSource<RunnerInputReleaseException?>? _idleCompletion;
    private bool _disposed;

    internal StandaloneInputPlayer(IRunnerInputNativeFacade? native = null)
    {
        _native = native ?? new WindowsRunnerInputNativeFacade();
    }

    internal bool IsPlaying
    {
        get
        {
            lock (_gate)
            {
                return _playbackCancellation is not null;
            }
        }
    }

    internal bool HasUnreleasedInputs
    {
        get
        {
            lock (_gate)
            {
                return _heldKeys.Count != 0 || _heldButtons.Count != 0;
            }
        }
    }

    internal async Task PlayAsync(
        IReadOnlyList<RunnerInputAction> actions,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(actions);

        CancellationTokenSource linked;
        TaskCompletionSource<RunnerInputReleaseException?> completion;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_playbackCancellation is not null)
            {
                throw new InvalidOperationException("Playback is already running.");
            }

            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _playbackCancellation = linked;
            _idleCompletion = completion;
        }

        Exception? playbackFailure = null;
        RunnerInputReleaseException? releaseFailure = null;
        try
        {
            try
            {
                await Task.Run(() => PlayCoreAsync(actions, progress, linked.Token), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                playbackFailure = exception;
            }
        }
        finally
        {
            releaseFailure = ReleaseAllHeldInputs();
            lock (_gate)
            {
                if (ReferenceEquals(_playbackCancellation, linked))
                {
                    _playbackCancellation = null;
                }

                if (ReferenceEquals(_idleCompletion, completion))
                {
                    _idleCompletion = null;
                }
            }

            linked.Dispose();
            completion.TrySetResult(releaseFailure);
        }

        if (releaseFailure is not null)
        {
            throw playbackFailure is null
                ? releaseFailure
                : releaseFailure.WithPlaybackFailure(playbackFailure);
        }

        if (playbackFailure is not null)
        {
            ExceptionDispatchInfo.Capture(playbackFailure).Throw();
        }
    }

    /// <summary>
    /// Cancels playback and immediately attempts release. A non-null result means one or more
    /// inputs remain tracked and Windows rejected their release packets.
    /// </summary>
    internal RunnerInputReleaseException? StopAndRelease()
    {
        lock (_gate)
        {
            _playbackCancellation?.Cancel();
            return ReleaseAllHeldInputsLocked();
        }
    }

    /// <summary>
    /// Cancels playback, waits for the worker's finally block, then makes one final release attempt.
    /// </summary>
    internal async Task<RunnerInputReleaseException?> StopAsync()
    {
        Task<RunnerInputReleaseException?>? completionTask;
        lock (_gate)
        {
            _playbackCancellation?.Cancel();
            _ = ReleaseAllHeldInputsLocked();
            completionTask = _idleCompletion?.Task;
        }

        if (completionTask is not null)
        {
            _ = await completionTask.ConfigureAwait(false);
        }

        return ReleaseAllHeldInputs();
    }

    private async Task PlayCoreAsync(
        IReadOnlyList<RunnerInputAction> actions,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();
        TimeSpan total = actions.Count == 0 ? TimeSpan.Zero : actions[^1].Offset;
        TimeSpan lastProgressReport = TimeSpan.MinValue;

        for (int index = 0; index < actions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunnerInputAction action = actions[index];
            await WaitUntilAsync(clock, action.Offset, cancellationToken).ConfigureAwait(false);
            Send(action, cancellationToken);

            double value = total <= TimeSpan.Zero
                ? (index + 1d) / Math.Max(1, actions.Count)
                : Math.Clamp(action.Offset.TotalMilliseconds / total.TotalMilliseconds, 0d, 1d);
            if (progress is not null &&
                (index == actions.Count - 1 ||
                 lastProgressReport == TimeSpan.MinValue ||
                 clock.Elapsed - lastProgressReport >= TimeSpan.FromMilliseconds(50)))
            {
                progress.Report(value);
                lastProgressReport = clock.Elapsed;
            }
        }

        progress?.Report(1d);
    }

    private static async Task WaitUntilAsync(
        Stopwatch clock,
        TimeSpan target,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan remaining = target - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            if (remaining > TimeSpan.FromMilliseconds(3))
            {
                TimeSpan delay = remaining - TimeSpan.FromMilliseconds(1);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Thread.SpinWait(64);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private void Send(RunnerInputAction action, CancellationToken cancellationToken)
    {
        // The cancellation check and injected event are serialized with cleanup. If stop wins the
        // lock, no new key/button-down can be sent after release; if playback wins, stop sees and
        // releases the newly tracked input.
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (action.Kind)
            {
                case RunnerInputActionKind.MouseMove:
                    SendMouseMoveLocked(action.X, action.Y);
                    break;
                case RunnerInputActionKind.LeftButtonDown:
                    SendMouseButtonLocked(action, NativeMethods.MouseEventLeftDown, HeldMouseButton.Left, isDown: true);
                    break;
                case RunnerInputActionKind.LeftButtonUp:
                    SendMouseButtonLocked(action, NativeMethods.MouseEventLeftUp, HeldMouseButton.Left, isDown: false);
                    break;
                case RunnerInputActionKind.RightButtonDown:
                    SendMouseButtonLocked(action, NativeMethods.MouseEventRightDown, HeldMouseButton.Right, isDown: true);
                    break;
                case RunnerInputActionKind.RightButtonUp:
                    SendMouseButtonLocked(action, NativeMethods.MouseEventRightUp, HeldMouseButton.Right, isDown: false);
                    break;
                case RunnerInputActionKind.MiddleButtonDown:
                    SendMouseButtonLocked(action, NativeMethods.MouseEventMiddleDown, HeldMouseButton.Middle, isDown: true);
                    break;
                case RunnerInputActionKind.MiddleButtonUp:
                    SendMouseButtonLocked(action, NativeMethods.MouseEventMiddleUp, HeldMouseButton.Middle, isDown: false);
                    break;
                case RunnerInputActionKind.X1ButtonDown:
                    SendMouseButtonLocked(action, NativeMethods.MouseEventXDown, HeldMouseButton.X1, isDown: true, NativeMethods.XButton1);
                    break;
                case RunnerInputActionKind.X1ButtonUp:
                    SendMouseButtonLocked(action, NativeMethods.MouseEventXUp, HeldMouseButton.X1, isDown: false, NativeMethods.XButton1);
                    break;
                case RunnerInputActionKind.X2ButtonDown:
                    SendMouseButtonLocked(action, NativeMethods.MouseEventXDown, HeldMouseButton.X2, isDown: true, NativeMethods.XButton2);
                    break;
                case RunnerInputActionKind.X2ButtonUp:
                    SendMouseButtonLocked(action, NativeMethods.MouseEventXUp, HeldMouseButton.X2, isDown: false, NativeMethods.XButton2);
                    break;
                case RunnerInputActionKind.MouseWheel:
                    SendMouseWheelLocked(action, NativeMethods.MouseEventWheel);
                    break;
                case RunnerInputActionKind.HorizontalWheel:
                    SendMouseWheelLocked(action, NativeMethods.MouseEventHorizontalWheel);
                    break;
                case RunnerInputActionKind.KeyDown:
                    SendKeyboardLocked(action, isDown: true);
                    break;
                case RunnerInputActionKind.KeyUp:
                    SendKeyboardLocked(action, isDown: false);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported runner action: {action.Kind}.");
            }
        }
    }

    private void SendMouseWheelLocked(RunnerInputAction action, uint flag)
    {
        SendMouseMoveLocked(action.X, action.Y);
        _native.Send(CreateMouseInput(0, 0, unchecked((uint)action.Data), flag));
    }

    private void SendMouseMoveLocked(int x, int y)
    {
        int left = _native.GetSystemMetric(NativeMethods.SmXVirtualScreen);
        int top = _native.GetSystemMetric(NativeMethods.SmYVirtualScreen);
        int width = Math.Max(1, _native.GetSystemMetric(NativeMethods.SmCxVirtualScreen));
        int height = Math.Max(1, _native.GetSystemMetric(NativeMethods.SmCyVirtualScreen));

        int normalizedX = NormalizeAbsoluteCoordinate(x, left, width);
        int normalizedY = NormalizeAbsoluteCoordinate(y, top, height);
        _native.Send(CreateMouseInput(
            normalizedX,
            normalizedY,
            0,
            NativeMethods.MouseEventMove | NativeMethods.MouseEventAbsolute | NativeMethods.MouseEventVirtualDesk));
    }

    internal static int NormalizeAbsoluteCoordinate(int coordinate, int origin, int extent)
    {
        if (extent <= 1)
        {
            return 0;
        }

        long relative = Math.Clamp((long)coordinate - origin, 0L, extent - 1L);
        return (int)Math.Round(relative * 65535d / (extent - 1d), MidpointRounding.AwayFromZero);
    }

    private void SendMouseButtonLocked(
        RunnerInputAction action,
        uint flag,
        HeldMouseButton button,
        bool isDown,
        uint data = 0)
    {
        SendMouseMoveLocked(action.X, action.Y);
        _native.Send(CreateMouseInput(0, 0, data, flag));
        if (isDown)
        {
            _heldButtons.Add(button);
        }
        else
        {
            _heldButtons.Remove(button);
        }
    }

    private void SendKeyboardLocked(RunnerInputAction action, bool isDown)
    {
        HeldKey key = new(action.VirtualKey, action.ScanCode, action.IsExtendedKey);
        _native.Send(CreateKeyboardInput(key, isDown));
        if (isDown)
        {
            _heldKeys.Add(key);
        }
        else
        {
            _heldKeys.Remove(key);
        }
    }

    private RunnerInputReleaseException? ReleaseAllHeldInputs()
    {
        lock (_gate)
        {
            return ReleaseAllHeldInputsLocked();
        }
    }

    private RunnerInputReleaseException? ReleaseAllHeldInputsLocked()
    {
        List<Exception> failures = [];
        foreach (HeldKey key in _heldKeys.ToArray())
        {
            try
            {
                _native.Send(CreateKeyboardInput(key, isDown: false));
                _heldKeys.Remove(key);
            }
            catch (Exception exception)
            {
                failures.Add(new InvalidOperationException(
                    "Windows rejected a keyboard release event.", exception));
            }
        }

        foreach (HeldMouseButton button in _heldButtons.ToArray())
        {
            uint flag = button switch
            {
                HeldMouseButton.Left => NativeMethods.MouseEventLeftUp,
                HeldMouseButton.Right => NativeMethods.MouseEventRightUp,
                HeldMouseButton.Middle => NativeMethods.MouseEventMiddleUp,
                HeldMouseButton.X1 => NativeMethods.MouseEventXUp,
                HeldMouseButton.X2 => NativeMethods.MouseEventXUp,
                _ => 0,
            };
            uint data = button switch
            {
                HeldMouseButton.X1 => NativeMethods.XButton1,
                HeldMouseButton.X2 => NativeMethods.XButton2,
                _ => 0,
            };

            try
            {
                _native.Send(CreateMouseInput(0, 0, data, flag));
                _heldButtons.Remove(button);
            }
            catch (Exception exception)
            {
                failures.Add(new InvalidOperationException(
                    "Windows rejected a mouse-button release event.", exception));
            }
        }

        int remainingInputCount = _heldKeys.Count + _heldButtons.Count;
        return remainingInputCount == 0
            ? null
            : new RunnerInputReleaseException(remainingInputCount, failures);
    }

    private static NativeMethods.Input CreateKeyboardInput(HeldKey key, bool isDown)
    {
        uint flags = isDown ? 0u : NativeMethods.KeyEventKeyUp;
        if (key.ScanCode != 0)
        {
            flags |= NativeMethods.KeyEventScanCode;
        }

        if (key.IsExtended)
        {
            flags |= NativeMethods.KeyEventExtendedKey;
        }

        return new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            Data = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    VirtualKey = key.ScanCode == 0 ? key.VirtualKey : (ushort)0,
                    ScanCode = key.ScanCode,
                    Flags = flags,
                },
            },
        };
    }

    private static NativeMethods.Input CreateMouseInput(int x, int y, uint data, uint flags) =>
        new()
        {
            Type = NativeMethods.InputMouse,
            Data = new NativeMethods.InputUnion
            {
                Mouse = new NativeMethods.MouseInput
                {
                    Dx = x,
                    Dy = y,
                    MouseData = data,
                    Flags = flags,
                },
            },
        };

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _ = StopAsync().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    private readonly record struct HeldKey(ushort VirtualKey, ushort ScanCode, bool IsExtended);

    private enum HeldMouseButton
    {
        Left,
        Right,
        Middle,
        X1,
        X2,
    }
}
