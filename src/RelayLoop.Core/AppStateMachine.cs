namespace RelayLoop.Core;

public enum AppState
{
    Idle,
    Recording,
    Playing,
    Stopping,
    Error,
}

public sealed class AppStateChangedEventArgs : EventArgs
{
    public AppStateChangedEventArgs(
        AppState previousState,
        AppState currentState,
        string? errorMessage,
        long transitionVersion)
    {
        PreviousState = previousState;
        CurrentState = currentState;
        ErrorMessage = errorMessage;
        TransitionVersion = transitionVersion;
    }

    public AppState PreviousState { get; }

    public AppState CurrentState { get; }

    public string? ErrorMessage { get; }

    public long TransitionVersion { get; }
}

/// <summary>
/// Serializes application lifecycle transitions. In particular, only one caller can leave Idle
/// for Recording or Playing, even when hotkeys and UI commands race on different threads.
/// </summary>
public sealed class AppStateMachine
{
    private readonly object _sync = new();
    private AppState _state = AppState.Idle;
    private string? _errorMessage;
    private long _transitionVersion;

    public event EventHandler<AppStateChangedEventArgs>? StateChanged;

    public AppState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public string? ErrorMessage
    {
        get
        {
            lock (_sync)
            {
                return _errorMessage;
            }
        }
    }

    public long TransitionVersion
    {
        get
        {
            lock (_sync)
            {
                return _transitionVersion;
            }
        }
    }

    public bool CanRecord => State == AppState.Idle;

    public bool CanPlay => State == AppState.Idle;

    public bool CanStop => State is AppState.Recording or AppState.Playing;

    public bool TryBeginRecording(out string? failureReason) =>
        TryTransition(AppState.Recording, errorMessage: null, out failureReason);

    public bool TryBeginPlayback(out string? failureReason) =>
        TryTransition(AppState.Playing, errorMessage: null, out failureReason);

    public bool TryRequestStop(out string? failureReason) =>
        TryTransition(AppState.Stopping, errorMessage: null, out failureReason);

    public bool TryCompleteStop(out string? failureReason) =>
        TryTransitionFrom(AppState.Stopping, AppState.Idle, errorMessage: null, out failureReason);

    public bool TryResetError(out string? failureReason) =>
        TryTransitionFrom(AppState.Error, AppState.Idle, errorMessage: null, out failureReason);

    public void BeginRecording() => TransitionOrThrow(AppState.Recording);

    public void BeginPlayback() => TransitionOrThrow(AppState.Playing);

    public void RequestStop() => TransitionOrThrow(AppState.Stopping);

    public void CompleteStop() => TransitionFromOrThrow(AppState.Stopping, AppState.Idle);

    public void ResetError() => TransitionFromOrThrow(AppState.Error, AppState.Idle);

    public void SetError(string errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
        if (!TryTransition(AppState.Error, errorMessage, out var reason))
        {
            throw new InvalidOperationException(reason);
        }
    }

    public void SetError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        SetError(exception.Message);
    }

    public bool TryTransition(
        AppState targetState,
        string? errorMessage,
        out string? failureReason) =>
        TryTransitionCore(requiredSource: null, targetState, errorMessage, out failureReason);

    private bool TryTransitionFrom(
        AppState requiredSource,
        AppState targetState,
        string? errorMessage,
        out string? failureReason) =>
        TryTransitionCore(requiredSource, targetState, errorMessage, out failureReason);

    private bool TryTransitionCore(
        AppState? requiredSource,
        AppState targetState,
        string? errorMessage,
        out string? failureReason)
    {
        if (!Enum.IsDefined(targetState))
        {
            throw new ArgumentOutOfRangeException(nameof(targetState));
        }

        if (targetState == AppState.Error && string.IsNullOrWhiteSpace(errorMessage))
        {
            throw new ArgumentException("An error transition requires a message.", nameof(errorMessage));
        }

        AppStateChangedEventArgs? eventArgs;
        EventHandler<AppStateChangedEventArgs>? handler;
        lock (_sync)
        {
            if (requiredSource.HasValue && _state != requiredSource.Value)
            {
                failureReason = $"Cannot complete a {requiredSource.Value} operation while the application is {_state}.";
                return false;
            }

            if (!IsAllowed(_state, targetState))
            {
                failureReason = $"Cannot transition from {_state} to {targetState}.";
                return false;
            }

            var previous = _state;
            _state = targetState;
            _errorMessage = targetState == AppState.Error ? errorMessage : null;
            _transitionVersion++;
            failureReason = null;
            eventArgs = new(previous, targetState, _errorMessage, _transitionVersion);
            handler = StateChanged;
        }

        handler?.Invoke(this, eventArgs);
        return true;
    }

    public bool TryTransition(AppState targetState, out string? failureReason) =>
        TryTransition(targetState, errorMessage: null, out failureReason);

    private static bool IsAllowed(AppState current, AppState target) => (current, target) switch
    {
        (AppState.Idle, AppState.Recording or AppState.Playing or AppState.Error) => true,
        (AppState.Recording, AppState.Stopping or AppState.Error) => true,
        (AppState.Playing, AppState.Stopping or AppState.Error) => true,
        (AppState.Stopping, AppState.Idle or AppState.Error) => true,
        (AppState.Error, AppState.Idle or AppState.Error) => true,
        _ => false,
    };

    private void TransitionOrThrow(AppState targetState)
    {
        if (!TryTransition(targetState, errorMessage: null, out var reason))
        {
            throw new InvalidOperationException(reason);
        }
    }

    private void TransitionFromOrThrow(AppState requiredSource, AppState targetState)
    {
        if (!TryTransitionFrom(requiredSource, targetState, errorMessage: null, out var reason))
        {
            throw new InvalidOperationException(reason);
        }
    }
}
