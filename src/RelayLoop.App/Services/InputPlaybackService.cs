using System.Diagnostics;
using System.Runtime.ExceptionServices;
using RelayLoop.Core;

namespace RelayLoop.App.Services;

public sealed record PlaybackOptions
{
    public double Speed { get; init; } = 1;

    public int RepeatCount { get; init; } = 1;

    public bool Continuous { get; init; }

    internal void Validate()
    {
        if (!double.IsFinite(Speed) || Speed is < 0.25 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(Speed), "Playback speed must be between 0.25x and 100x.");
        }

        if (RepeatCount is < 1 or > 9_999)
        {
            throw new ArgumentOutOfRangeException(nameof(RepeatCount), "Repeat count must be from 1 through 9999.");
        }
    }
}

public sealed class PlaybackProgressEventArgs(
    int loopNumber,
    int? totalLoops,
    int eventIndex,
    int enabledEventCount,
    long completedEventCount,
    TimeSpan estimatedRemaining) : EventArgs
{
    public int LoopNumber { get; } = loopNumber;

    public int? TotalLoops { get; } = totalLoops;

    public int EventIndex { get; } = eventIndex;

    public int EnabledEventCount { get; } = enabledEventCount;

    public long CompletedEventCount { get; } = completedEventCount;

    public TimeSpan EstimatedRemaining { get; } = estimatedRemaining;
}

public sealed class PlaybackCompletedEventArgs(bool wasCancelled, Exception? error) : EventArgs
{
    public bool WasCancelled { get; } = wasCancelled;

    public Exception? Error { get; } = error;
}

public interface IPlaybackTiming
{
    long GetTimestamp();

    long AddMicroseconds(long timestamp, double microseconds);

    ValueTask WaitUntilAsync(long targetTimestamp, CancellationToken cancellationToken);
}

/// <summary>A cancellable monotonic scheduler with a short final yield/spin for sub-timer precision.</summary>
public sealed class StopwatchPlaybackTiming : IPlaybackTiming
{
    private const double TimerGuardSeconds = 0.002;

    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public long AddMicroseconds(long timestamp, double microseconds)
    {
        if (!double.IsFinite(microseconds) || microseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(microseconds));
        }

        var ticks = microseconds * Stopwatch.Frequency / 1_000_000d;
        if (ticks > long.MaxValue - timestamp)
        {
            throw new ArgumentOutOfRangeException(nameof(microseconds), "The requested playback delay is too large.");
        }

        return timestamp + unchecked((long)Math.Round(ticks, MidpointRounding.AwayFromZero));
    }

    public async ValueTask WaitUntilAsync(long targetTimestamp, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingTicks = targetTimestamp - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                return;
            }

            var remainingSeconds = (double)remainingTicks / Stopwatch.Frequency;
            if (remainingSeconds > TimerGuardSeconds)
            {
                var coarseDelay = TimeSpan.FromSeconds(remainingSeconds - 0.001);
                await Task.Delay(coarseDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (remainingSeconds > 0.0002)
            {
                Thread.Yield();
            }
            else
            {
                Thread.SpinWait(32);
            }
        }
    }
}

public interface IInputPlaybackService : IDisposable, IAsyncDisposable
{
    event EventHandler<PlaybackProgressEventArgs>? ProgressChanged;

    event EventHandler<PlaybackCompletedEventArgs>? PlaybackCompleted;

    bool IsPlaying { get; }

    Task PlayAsync(
        IReadOnlyList<MacroEvent> macroEvents,
        PlaybackOptions options,
        CancellationToken cancellationToken = default);

    Task StopAsync();
}

/// <summary>
/// Schedules playback on a worker thread and releases every key/button still held by this playback
/// in a finally block, covering cancellation, injection errors, and application disposal.
/// </summary>
public sealed class InputPlaybackService : IInputPlaybackService
{
    private readonly IInputInjector _injector;
    private readonly IPlaybackTiming _timing;
    private readonly object _gate = new();
    private readonly HashSet<HeldKey> _pendingReleaseKeys = [];
    private readonly HashSet<MouseButton> _pendingReleaseButtons = [];
    private CancellationTokenSource? _activeCancellation;
    private Task? _activeTask;
    private bool _disposed;

    public InputPlaybackService(IInputInjector injector, IPlaybackTiming? timing = null)
    {
        _injector = injector ?? throw new ArgumentNullException(nameof(injector));
        _timing = timing ?? new StopwatchPlaybackTiming();
    }

    public event EventHandler<PlaybackProgressEventArgs>? ProgressChanged;

    public event EventHandler<PlaybackCompletedEventArgs>? PlaybackCompleted;

    public bool IsPlaying
    {
        get
        {
            lock (_gate)
            {
                return _activeTask is { IsCompleted: false };
            }
        }
    }

    public Task PlayAsync(
        IReadOnlyList<MacroEvent> macroEvents,
        PlaybackOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(macroEvents);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        lock (_gate)
        {
            if (_activeTask is { IsCompleted: false })
            {
                throw new InvalidOperationException("Playback is already active.");
            }

            var pendingReleaseFailure = RetryPendingReleasesLocked();
            if (pendingReleaseFailure is not null)
            {
                throw pendingReleaseFailure;
            }

            _activeCancellation?.Dispose();
            _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var activeCancellation = _activeCancellation;
            var optionsSnapshot = options with { };
            _activeTask = Task.Run(
                () => PlaybackWorkerAsync(macroEvents, optionsSnapshot, activeCancellation.Token),
                CancellationToken.None);
            return _activeTask;
        }
    }

    public async Task StopAsync()
    {
        Task? task;
        CancellationTokenSource? cancellation;
        Exception? pendingReleaseFailure = null;
        lock (_gate)
        {
            task = _activeTask;
            cancellation = _activeCancellation;
            if (task is null)
            {
                pendingReleaseFailure = RetryPendingReleasesLocked();
            }
            else if (task.IsCompleted)
            {
                _activeTask = null;
                _activeCancellation = null;
                pendingReleaseFailure = RetryPendingReleasesLocked();
            }
            else
            {
                cancellation?.Cancel();
            }
        }

        if (task is null)
        {
            if (pendingReleaseFailure is not null)
            {
                throw pendingReleaseFailure;
            }

            return;
        }

        if (task.IsCompleted)
        {
            cancellation?.Dispose();
            if (pendingReleaseFailure is not null)
            {
                throw pendingReleaseFailure;
            }

            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the expected result of StopAsync.
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeTask, task))
                {
                    _activeTask = null;
                    _activeCancellation = null;
                }
            }

            cancellation?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TryStopAndReleaseForDisposalAsync().GetAwaiter().GetResult();
        lock (_gate)
        {
            _activeCancellation?.Dispose();
            _activeCancellation = null;
        }

        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await TryStopAndReleaseForDisposalAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _activeCancellation?.Dispose();
            _activeCancellation = null;
        }

        GC.SuppressFinalize(this);
    }

    private async Task PlaybackWorkerAsync(
        IReadOnlyList<MacroEvent> macroEvents,
        PlaybackOptions options,
        CancellationToken cancellationToken)
    {
        var heldKeys = new HashSet<HeldKey>();
        var heldButtons = new HashSet<MouseButton>();
        Exception? failure = null;
        var wasCancelled = false;

        try
        {
            var snapshot = CreateValidatedSnapshot(macroEvents, cancellationToken);
            var enabledEventCount = snapshot.Count(static item => item.Enabled);
            if (enabledEventCount == 0)
            {
                return;
            }

            var loopDurationMicroseconds = snapshot.Sum(static item => (decimal)item.DelayMicroseconds);
            int? totalLoops = options.Continuous ? null : options.RepeatCount;
            long completedEventCount = 0;
            var loopNumber = 0;
            var targetTimestamp = _timing.GetTimestamp();
            var zeroDelayContinuous = options.Continuous && loopDurationMicroseconds == 0;

            while (options.Continuous || loopNumber < options.RepeatCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                loopNumber++;

                for (var index = 0; index < snapshot.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var macroEvent = snapshot[index];
                    targetTimestamp = _timing.AddMicroseconds(
                        targetTimestamp,
                        macroEvent.DelayMicroseconds / options.Speed);
                    await _timing.WaitUntilAsync(targetTimestamp, cancellationToken).ConfigureAwait(false);

                    if (!macroEvent.Enabled)
                    {
                        continue;
                    }

                    _injector.Inject(macroEvent);
                    UpdateHeldState(macroEvent, heldKeys, heldButtons);
                    completedEventCount++;

                    var remaining = CalculateRemaining(
                        options,
                        loopDurationMicroseconds,
                        snapshot,
                        loopNumber,
                        index);
                    RaiseProgress(new PlaybackProgressEventArgs(
                        loopNumber,
                        totalLoops,
                        index,
                        enabledEventCount,
                        completedEventCount,
                        remaining));

                    if (zeroDelayContinuous && (completedEventCount & 0xFF) == 0)
                    {
                        await Task.Yield();
                    }
                }

                if (zeroDelayContinuous)
                {
                    await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            wasCancelled = true;
            failure = exception;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            var releaseFailure = ReleaseHeldInputs(heldKeys, heldButtons);
            if (releaseFailure is not null)
            {
                failure = failure is null
                    ? releaseFailure
                    : new AggregateException("Playback failed and one or more held inputs could not be released.", failure, releaseFailure);
            }

            RaiseCompleted(new PlaybackCompletedEventArgs(wasCancelled, failure));
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static IReadOnlyList<MacroEvent> CreateValidatedSnapshot(
        IReadOnlyList<MacroEvent> macroEvents,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = macroEvents.Count;
        if (count > MacroValidator.MaxEventCount)
        {
            throw new MacroValidationException([
                new MacroValidationIssue("$.events", $"No more than {MacroValidator.MaxEventCount:N0} events are allowed.")]);
        }

        var events = new List<MacroEvent>(count);
        for (var index = 0; index < count; index++)
        {
            if ((index & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var macroEvent = macroEvents[index] ??
                throw new MacroValidationException([new MacroValidationIssue($"$.events[{index}]", "Event must not be null.")]);
            events.Add(macroEvent.DeepClone());
        }

        var validationDocument = new MacroDocument
        {
            DisplayLayout = new DisplayLayout
            {
                VirtualLeft = 0,
                VirtualTop = 0,
                VirtualWidth = 1,
                VirtualHeight = 1,
                Monitors =
                [
                    new MonitorInfo
                    {
                        DeviceName = @"\\.\DISPLAY1",
                        Left = 0,
                        Top = 0,
                        Width = 1,
                        Height = 1,
                        DpiX = 96,
                        DpiY = 96,
                        IsPrimary = true,
                    },
                ],
            },
            Events = events,
        };
        MacroValidator.Validate(validationDocument, cancellationToken);
        return events;
    }

    private static void UpdateHeldState(
        MacroEvent macroEvent,
        ISet<HeldKey> heldKeys,
        ISet<MouseButton> heldButtons)
    {
        var key = new HeldKey(macroEvent.VirtualKey, macroEvent.ScanCode, macroEvent.IsExtendedKey);
        switch (macroEvent.Kind)
        {
            case MacroEventKind.KeyDown:
                heldKeys.Add(key);
                break;
            case MacroEventKind.KeyUp:
                heldKeys.Remove(key);
                break;
            case MacroEventKind.MouseButtonDown:
                heldButtons.Add(macroEvent.Button);
                break;
            case MacroEventKind.MouseButtonUp:
                heldButtons.Remove(macroEvent.Button);
                break;
        }
    }

    private Exception? ReleaseHeldInputs(
        ISet<HeldKey> heldKeys,
        ISet<MouseButton> heldButtons)
    {
        List<Exception>? failures = null;
        foreach (var key in heldKeys.Reverse().ToArray())
        {
            try
            {
                _injector.ReleaseKey(key);
                heldKeys.Remove(key);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        foreach (var button in heldButtons.Reverse().ToArray())
        {
            try
            {
                _injector.ReleaseMouseButton(button);
                heldButtons.Remove(button);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        lock (_gate)
        {
            _pendingReleaseKeys.UnionWith(heldKeys);
            _pendingReleaseButtons.UnionWith(heldButtons);
        }

        return failures is null
            ? null
            : new AggregateException(
                $"One or more held inputs could not be released; {heldKeys.Count + heldButtons.Count} release(s) remain pending.",
                failures);
    }

    private AggregateException? RetryPendingReleasesLocked()
    {
        List<Exception>? failures = null;
        foreach (var key in _pendingReleaseKeys.ToArray())
        {
            try
            {
                _injector.ReleaseKey(key);
                _pendingReleaseKeys.Remove(key);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        foreach (var button in _pendingReleaseButtons.ToArray())
        {
            try
            {
                _injector.ReleaseMouseButton(button);
                _pendingReleaseButtons.Remove(button);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        return failures is null
            ? null
            : new AggregateException(
                $"Windows still rejected {_pendingReleaseKeys.Count + _pendingReleaseButtons.Count} held-input release(s).",
                failures);
    }

    private async Task TryStopAndReleaseForDisposalAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // StopAsync has already tracked every failed release. Make one final retry below; a
            // disposal path must finish tearing down even when Windows continues to reject input.
        }

        lock (_gate)
        {
            _ = RetryPendingReleasesLocked();
        }
    }

    private static TimeSpan CalculateRemaining(
        PlaybackOptions options,
        decimal loopDurationMicroseconds,
        IReadOnlyList<MacroEvent> macroEvents,
        int loopNumber,
        int currentIndex)
    {
        if (options.Continuous)
        {
            return Timeout.InfiniteTimeSpan;
        }

        decimal remainingInLoop = 0;
        for (var index = currentIndex + 1; index < macroEvents.Count; index++)
        {
            remainingInLoop += macroEvents[index].DelayMicroseconds;
        }
        var remainingLoops = Math.Max(0, options.RepeatCount - loopNumber);
        var remainingMicroseconds = (remainingInLoop + (loopDurationMicroseconds * remainingLoops)) / (decimal)options.Speed;
        var ticks = Math.Min((decimal)TimeSpan.MaxValue.Ticks, remainingMicroseconds * 10m);
        return TimeSpan.FromTicks(unchecked((long)ticks));
    }

    private void RaiseProgress(PlaybackProgressEventArgs args)
    {
        try
        {
            ProgressChanged?.Invoke(this, args);
        }
        catch
        {
            // UI observers must not interrupt playback or prevent held-input cleanup.
        }
    }

    private void RaiseCompleted(PlaybackCompletedEventArgs args)
    {
        try
        {
            PlaybackCompleted?.Invoke(this, args);
        }
        catch
        {
            // Completion observers must not interfere with cleanup.
        }
    }
}
