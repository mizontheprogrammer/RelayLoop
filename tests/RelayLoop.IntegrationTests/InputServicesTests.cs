using System.Collections.Concurrent;
using RelayLoop.App.Native;
using RelayLoop.App.Services;
using RelayLoop.Core;
using Xunit;
using AppPlaybackOptions = RelayLoop.App.Services.PlaybackOptions;

namespace RelayLoop.IntegrationTests;

public sealed class InputServicesTests
{
    [Fact]
    public void Recorder_CapturesTimedMouseAndKeyboardSequence()
    {
        FakeInputSource source = new();
        FakeClock clock = new();
        using FakeSensitiveGuard guard = new(SensitiveInputState.Allowed);
        using InputRecorderService recorder = new(source, clock, sensitiveInputGuard: guard);

        recorder.Start();
        clock.Timestamp = 1_000;
        source.RaiseMouse(MouseWindowMessage.Move, x: -320, y: 240);
        clock.Timestamp = 2_500;
        source.RaiseMouse(MouseWindowMessage.LeftButtonDown, x: -320, y: 240);
        clock.Timestamp = 4_000;
        source.RaiseMouse(MouseWindowMessage.LeftButtonUp, x: -320, y: 240);
        clock.Timestamp = 5_000;
        source.RaiseKeyboard(KeyboardWindowMessage.KeyDown, 0x41, 0x1E);
        clock.Timestamp = 7_500;
        source.RaiseKeyboard(KeyboardWindowMessage.KeyUp, 0x41, 0x1E);

        IReadOnlyList<MacroEvent> events = recorder.Stop();

        Assert.Equal(5, events.Count);
        Assert.Equal(
            [
                MacroEventKind.MouseMove,
                MacroEventKind.MouseButtonDown,
                MacroEventKind.MouseButtonUp,
                MacroEventKind.KeyDown,
                MacroEventKind.KeyUp,
            ],
            events.Select(static item => item.Kind));
        Assert.Equal([1_000L, 1_500L, 1_500L, 1_000L, 2_500L], events.Select(static item => item.DelayMicroseconds));
        Assert.Equal(-320, events[0].X);
        Assert.Equal(0x41, events[3].VirtualKey);
        Assert.False(source.IsRunning);
    }

    [Fact]
    public void Recorder_ControlChordStopsWithoutContaminatingMacro()
    {
        FakeInputSource source = new();
        FakeClock clock = new();
        using FakeSensitiveGuard guard = new(SensitiveInputState.Allowed);
        using InputRecorderService recorder = new(source, clock, sensitiveInputGuard: guard);
        var stopRequests = 0;
        recorder.EmergencyStopRequested += (_, _) => stopRequests++;

        recorder.Start();
        source.RaiseKeyboard(KeyboardWindowMessage.KeyDown, 0x11, 0x1D);
        source.RaiseKeyboard(KeyboardWindowMessage.KeyDown, 0x10, 0x2A);
        source.RaiseKeyboard(KeyboardWindowMessage.SystemKeyDown, 0x12, 0x38);
        source.RaiseKeyboard(KeyboardWindowMessage.KeyDown, 0x52, 0x13);

        Assert.False(recorder.IsRecording);
        Assert.Empty(recorder.Snapshot);
        Assert.Equal(1, stopRequests);
        Assert.False(source.IsRunning);
    }

    [Fact]
    public void Recorder_IgnoresKeyReleasesWhosePressPredatedCapture()
    {
        FakeInputSource source = new();
        using FakeSensitiveGuard guard = new(SensitiveInputState.Allowed);
        using InputRecorderService recorder = new(source, new FakeClock(), sensitiveInputGuard: guard);

        recorder.Start();
        source.RaiseKeyboard(KeyboardWindowMessage.KeyUp, 0x11, 0x1D);
        source.RaiseKeyboard(KeyboardWindowMessage.KeyUp, 0x10, 0x2A);
        source.RaiseKeyboard(KeyboardWindowMessage.SystemKeyUp, 0x12, 0x38);

        Assert.Empty(recorder.Stop());
    }

    [Fact]
    public void Recorder_BlocksKeyboardAndPointerOnCredentialSurface()
    {
        FakeInputSource source = new();
        FakeClock clock = new();
        using FakeSensitiveGuard guard = new(new SensitiveInputState(false, SensitiveInputBlockReason.PasswordField));
        using InputRecorderService recorder = new(source, clock, sensitiveInputGuard: guard);
        var blockedNotifications = 0;
        recorder.SensitiveInputBlocked += (_, _) => blockedNotifications++;

        recorder.Start();
        source.RaiseKeyboard(KeyboardWindowMessage.KeyDown, 0x41, 0x1E);
        source.RaiseKeyboard(KeyboardWindowMessage.KeyUp, 0x41, 0x1E);
        source.RaiseMouse(MouseWindowMessage.LeftButtonDown, 10, 10);
        source.RaiseMouse(MouseWindowMessage.LeftButtonUp, 10, 10);

        Assert.Empty(recorder.Stop());
        Assert.Equal(1, blockedNotifications);
    }

    [Fact]
    public void Recorder_StopsAtConfiguredSafetyLimit()
    {
        FakeInputSource source = new();
        FakeClock clock = new();
        using FakeSensitiveGuard guard = new(SensitiveInputState.Allowed);
        using InputRecorderService recorder = new(source, clock, sensitiveInputGuard: guard, maximumEventCount: 2);
        Exception? fault = null;
        recorder.Faulted += (_, exception) => fault = exception;

        recorder.Start();
        source.RaiseMouse(MouseWindowMessage.Move, 1, 1);
        source.RaiseMouse(MouseWindowMessage.Move, 2, 2);
        source.RaiseMouse(MouseWindowMessage.Move, 3, 3);

        Assert.False(recorder.IsRecording);
        Assert.Equal(2, recorder.Snapshot.Count);
        Assert.IsType<RecordingLimitExceededException>(fault);
        Assert.False(source.IsRunning);
    }

    [Fact]
    public void Recorder_StartPermissionFailureRollsBackRecordingState()
    {
        FakeInputSource source = new() { StartFailure = new UnauthorizedAccessException("Hook denied.") };
        using FakeSensitiveGuard guard = new(SensitiveInputState.Allowed);
        using InputRecorderService recorder = new(source, new FakeClock(), sensitiveInputGuard: guard);

        Assert.Throws<UnauthorizedAccessException>(recorder.Start);

        Assert.False(recorder.IsRecording);
        Assert.False(source.IsRunning);
    }

    [Fact]
    public async Task Playback_PreservesDisabledDelayAndReplaysBasicSequence()
    {
        FakeInjector injector = new();
        FakePlaybackTiming timing = new();
        await using InputPlaybackService playback = new(injector, timing);
        MacroEvent[] events =
        [
            new() { Kind = MacroEventKind.MouseMove, DelayMicroseconds = 1_000, X = -10, Y = 20, Enabled = false },
            new() { Kind = MacroEventKind.KeyDown, DelayMicroseconds = 2_000, VirtualKey = 0x41, ScanCode = 0x1E },
            new() { Kind = MacroEventKind.KeyUp, DelayMicroseconds = 500, VirtualKey = 0x41, ScanCode = 0x1E },
        ];

        await playback.PlayAsync(events, new AppPlaybackOptions { Speed = 1, RepeatCount = 1 });

        Assert.Equal([1_000L, 3_000L, 3_500L], timing.WaitTargets);
        Assert.Equal([MacroEventKind.KeyDown, MacroEventKind.KeyUp], injector.Injected.Select(static item => item.Kind));
        Assert.Empty(injector.ReleasedKeys);
    }

    [Fact]
    public async Task Playback_EmergencyStopCancelsContinuousLoopAndReleasesHeldInput()
    {
        FakeInjector injector = new();
        await using InputPlaybackService playback = new(injector);
        Task running = playback.PlayAsync(
            [new MacroEvent { Kind = MacroEventKind.KeyDown, VirtualKey = 0x41, ScanCode = 0x1E }],
            new AppPlaybackOptions { Speed = 1, RepeatCount = 1, Continuous = true });
        await injector.FirstInjection.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await playback.StopAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Contains(injector.ReleasedKeys, static key => key.VirtualKey == 0x41);
        Assert.False(playback.IsPlaying);
    }

    [Fact]
    public async Task Playback_ReleaseFailureFaultsTask()
    {
        FakeInjector injector = new() { FailKeyRelease = true };
        await using InputPlaybackService playback = new(injector, new FakePlaybackTiming());

        AggregateException exception = await Assert.ThrowsAsync<AggregateException>(() => playback.PlayAsync(
            [new MacroEvent { Kind = MacroEventKind.KeyDown, VirtualKey = 0x41, ScanCode = 0x1E }],
            new AppPlaybackOptions()));

        Assert.Contains("held inputs", exception.Message, StringComparison.OrdinalIgnoreCase);

        injector.FailKeyRelease = false;
        await playback.StopAsync();
        Assert.Contains(injector.ReleasedKeys, static key => key.VirtualKey == 0x41);
    }

    [Fact]
    public async Task Playback_DisposeDuringContinuousPlaybackCancelsAndReleasesHeldInput()
    {
        FakeInjector injector = new();
        InputPlaybackService playback = new(injector);
        Task running = playback.PlayAsync(
            [new MacroEvent { Kind = MacroEventKind.KeyDown, VirtualKey = 0x41, ScanCode = 0x1E }],
            new AppPlaybackOptions { Continuous = true });
        await injector.FirstInjection.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await playback.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Contains(injector.ReleasedKeys, static key => key.VirtualKey == 0x41);
        Assert.False(playback.IsPlaying);
    }

    [Fact]
    public async Task Playback_PermissionFailureIsSurfacedWithoutLeavingHeldInputs()
    {
        FakeInjector injector = new() { InjectionFailure = new UnauthorizedAccessException("SendInput denied.") };
        await using InputPlaybackService playback = new(injector, new FakePlaybackTiming());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => playback.PlayAsync(
            [new MacroEvent { Kind = MacroEventKind.KeyDown, VirtualKey = 0x41, ScanCode = 0x1E }],
            new AppPlaybackOptions()));

        Assert.Empty(injector.ReleasedKeys);
        Assert.False(playback.IsPlaying);
    }

    [Fact]
    public async Task Playback_ValidatesWholeSnapshotBeforeFirstInjection()
    {
        FakeInjector injector = new();
        await using InputPlaybackService playback = new(injector, new FakePlaybackTiming());

        await Assert.ThrowsAsync<MacroValidationException>(() => playback.PlayAsync(
            [
                new MacroEvent { Kind = MacroEventKind.KeyDown, VirtualKey = 0x41, ScanCode = 0x1E },
                new MacroEvent { Kind = MacroEventKind.KeyDown, VirtualKey = 0 },
            ],
            new AppPlaybackOptions()));

        Assert.Empty(injector.Injected);
    }

    [Fact]
    public void GlobalHotkeyConflictProducesUsefulException()
    {
        using FakeHotKeyNative native = new(registerSucceeds: false, lastError: HotKeyRegistrationException.HotKeyAlreadyRegisteredError);
        using GlobalHotKeyService hotkeys = new(native);

        HotKeyRegistrationException exception = Assert.Throws<HotKeyRegistrationException>(() =>
            hotkeys.Register("record", HotKeyGesture.RecordDefault));

        Assert.True(exception.IsConflict);
        Assert.Contains("already in use", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Ctrl+F24")]
    [InlineData("Alt+Space")]
    [InlineData("Shift+Delete")]
    [InlineData("Win+Left")]
    [InlineData("Ctrl+VK 0xBA")]
    public void HotkeyTextRoundTripsSupportedAndRawVirtualKeys(string text)
    {
        HotKeyGesture original = HotKeyParser.Parse(text);

        HotKeyGesture roundTripped = HotKeyParser.Parse(original.ToString());

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void DisposingRecorderWhileActiveStopsHookSource()
    {
        FakeInputSource source = new();
        using FakeSensitiveGuard guard = new(SensitiveInputState.Allowed);
        InputRecorderService recorder = new(source, new FakeClock(), sensitiveInputGuard: guard);
        recorder.Start();

        recorder.Dispose();

        Assert.False(source.IsRunning);
        Assert.True(source.IsDisposed);
    }

    private sealed class FakeClock : IMonotonicClock
    {
        public long Timestamp { get; set; }
        public long Frequency => 1_000_000;
        public long GetTimestamp() => Timestamp;
    }

    private sealed class FakeSensitiveGuard(SensitiveInputState state) : ISensitiveInputGuard
    {
        public SensitiveInputState CurrentState { get; set; } = state;
        public void Dispose() { }
    }

    private sealed class FakeInputSource : ILowLevelInputSource
    {
        public event EventHandler<KeyboardHookEventArgs>? KeyboardInput;
        public event EventHandler<MouseHookEventArgs>? MouseInput;
        public event EventHandler<Exception>? Faulted;
        public bool IsRunning { get; private set; }
        public bool IsDisposed { get; private set; }
        public Exception? StartFailure { get; init; }
        public void Start()
        {
            if (StartFailure is not null)
            {
                throw StartFailure;
            }

            IsRunning = true;
        }
        public void Stop() => IsRunning = false;
        public void Dispose()
        {
            IsRunning = false;
            IsDisposed = true;
        }

        public void RaiseKeyboard(KeyboardWindowMessage message, uint virtualKey, uint scanCode) =>
            KeyboardInput?.Invoke(this, new KeyboardHookEventArgs(new NativeKeyboardEvent(
                message, virtualKey, scanCode, 0, 0, 0)));

        public void RaiseMouse(MouseWindowMessage message, int x, int y) =>
            MouseInput?.Invoke(this, new MouseHookEventArgs(new NativeMouseEvent(message, x, y, 0, 0, 0, 0)));

        public void RaiseFault(Exception exception) => Faulted?.Invoke(this, exception);
    }

    private sealed class FakePlaybackTiming : IPlaybackTiming
    {
        public List<long> WaitTargets { get; } = [];
        public long GetTimestamp() => 0;
        public long AddMicroseconds(long timestamp, double microseconds) => timestamp + (long)Math.Round(microseconds);
        public ValueTask WaitUntilAsync(long targetTimestamp, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WaitTargets.Add(targetTimestamp);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeInjector : IInputInjector
    {
        public List<MacroEvent> Injected { get; } = [];
        public List<HeldKey> ReleasedKeys { get; } = [];
        public List<MouseButton> ReleasedButtons { get; } = [];
        public TaskCompletionSource<bool> FirstInjection { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailKeyRelease { get; set; }
        public Exception? InjectionFailure { get; init; }

        public void Inject(MacroEvent macroEvent)
        {
            if (InjectionFailure is not null)
            {
                throw InjectionFailure;
            }

            Injected.Add(macroEvent.DeepClone());
            FirstInjection.TrySetResult(true);
        }

        public void ReleaseKey(HeldKey key)
        {
            if (FailKeyRelease)
            {
                throw new InvalidOperationException("Simulated key release failure.");
            }

            ReleasedKeys.Add(key);
        }

        public void ReleaseMouseButton(MouseButton button) => ReleasedButtons.Add(button);
    }

    private sealed class FakeHotKeyNative(bool registerSucceeds, int lastError) : IHotKeyNativeFacade, IDisposable
    {
        private readonly BlockingCollection<NativeMessage> _messages = [];

        public uint GetCurrentThreadId() => 1;
        public bool PeekMessage(out NativeMessage message, nint window, uint minimum, uint maximum, uint removeMessage)
        {
            message = default;
            return true;
        }

        public int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum)
        {
            message = _messages.Take();
            return message.Message == 0x0012 ? 0 : 1;
        }

        public bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam)
        {
            _messages.Add(new NativeMessage { Message = message, WParam = wParam, LParam = lParam });
            return true;
        }

        public bool RegisterHotKey(nint window, int id, HotKeyModifiers modifiers, uint virtualKey) => registerSucceeds;
        public bool UnregisterHotKey(nint window, int id) => true;
        public int GetLastError() => lastError;
        public void Dispose() => _messages.Dispose();
    }
}
