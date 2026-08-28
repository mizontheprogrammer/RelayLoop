using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using RelayLoop.App.Models;
using RelayLoop.App.Mvvm;
using RelayLoop.App.Native;
using RelayLoop.App.Services;
using RelayLoop.Core;
using CorePlaybackOptions = RelayLoop.Core.PlaybackOptions;
using ServicePlaybackOptions = RelayLoop.App.Services.PlaybackOptions;

namespace RelayLoop.App.ViewModels;

/// <summary>
/// Coordinates the visible WPF shell with the isolated hook, hotkey, persistence, and playback
/// services. Native callbacks only enqueue immutable data; WPF collections are updated in batches
/// by the dispatcher timer so low-level hooks never wait for the UI.
/// </summary>
public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const double CompactHeight = 94;
    private const double ExpandedHeight = 640;
    private const double ErrorBannerHeight = 64;
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(2);

    private readonly SettingsService _settingsService;
    private readonly ThemeService _themeService = new();
    private readonly IUserDialogService _dialogs;
    private readonly IDisplayLayoutService _displayLayouts;
    private readonly RecoveryService _recovery;
    private readonly ProfileService _profileService;
    private readonly RunnerExportService _runnerExporter;
    private readonly InputRecorderService _recorder;
    private readonly IInputBindingCaptureService _inputCapture;
    private readonly IInputPlaybackService _playback;
    private readonly CursorLockService _cursorLock;
    private readonly AppStateMachine _stateMachine = new();
    private readonly EditorHistory<MacroDocument> _history;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _uiTimer;
    private readonly Stopwatch _activityStopwatch = new();
    private readonly ConcurrentQueue<MacroEvent> _recordedEventQueue = new();
    private readonly SemaphoreSlim _recoveryWriteGate = new(1, 1);
    private readonly object _shutdownGate = new();
    private readonly StructuredLogger? _logger;

    private AppSettings _settings = new();
    private MacroDocument _document = new();
    private IGlobalHotKeyService? _hotKeyService;
    private IGlobalHotKeyRegistration? _recordRegistration;
    private IGlobalHotKeyRegistration? _playRegistration;
    private IGlobalHotKeyRegistration? _pauseRegistration;
    private IGlobalHotKeyRegistration? _stopRegistration;
    private HotKeyGesture _recordGesture = HotKeyGesture.RecordDefault;
    private HotKeyGesture _playGesture = HotKeyGesture.PlayDefault;
    private HotKeyGesture _pauseGesture = HotKeyGesture.PauseDefault;
    private HotKeyGesture _stopGesture = HotKeyGesture.EmergencyStopDefault;
    private CancellationTokenSource? _countdownCancellation;
    private PlaybackProgressEventArgs? _pendingPlaybackProgress;
    private DisplayLayout? _recordingDisplayLayout;
    private DateTimeOffset _lastRecoveryWrite = DateTimeOffset.MinValue;
    private string? _currentPath;
    private string? _errorMessage;
    private string _displayLayoutStatus = "Display metadata will be captured with the recording.";
    private string _hotkeyStatusText = "Global hotkeys are initializing.";
    private string _loopText = "—";
    private string _remainingText = "—";
    private string _countdownActionText = string.Empty;
    private string _recordHotkeyText = HotKeyGesture.RecordDefault.ToString();
    private string _playHotkeyText = HotKeyGesture.PlayDefault.ToString();
    private string _pauseHotkeyText = HotKeyGesture.PauseDefault.ToString();
    private string _stopHotkeyText = HotKeyGesture.EmergencyStopDefault.ToString();
    private string _profileName = string.Empty;
    private string _profileStatusText = "Type a profile name to save this setup.";
    private EventRowViewModel? _selectedEvent;
    private StepRowViewModel? _selectedStep;
    private bool _isExpanded;
    private bool _isSettingsOpen;
    private bool _alwaysOnTop;
    private bool _countdownEnabled = true;
    private bool _continuousPlayback;
    private bool _isCountingDown;
    private bool _isDirty;
    private bool _initialized;
    private bool _updatingRows;
    private bool _closeInProgress;
    private bool _disposed;
    private bool _hasRunActivity;
    private bool _activeDirectionalHoldPreset;
    private bool _lockMouseDuringDirectionalHold;
    private int? _activeMouseLockX;
    private int? _activeMouseLockY;
    private int _countdownValue;
    private int _repeatCount = 1;
    private int _liveEventCount;
    private double _playbackSpeed = 1;
    private double _activePlaybackSpeed = 1;
    private double? _windowLeft;
    private double? _windowTop;

    public MainViewModel()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _settingsService = new SettingsService();
        _dialogs = new UserDialogService();
        _displayLayouts = new DisplayLayoutService();
        _recovery = new RecoveryService(_settingsService.BaseDirectory);
        _profileService = new ProfileService(_settingsService.BaseDirectory);
        _runnerExporter = new RunnerExportService();
        _recorder = new InputRecorderService(new WindowsLowLevelInputSource());
        _inputCapture = new InputBindingCaptureService(new WindowsLowLevelInputSource());
        _playback = new InputPlaybackService(new WindowsInputInjector());
        _cursorLock = new CursorLockService();
        _history = new EditorHistory<MacroDocument>(_document, static item => item.DeepClone(), capacity: 250);

        try
        {
            _logger = new StructuredLogger(_settingsService.BaseDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            _logger = null;
        }

        Events = [];
        Steps = [];
        Profiles = [];
        SpeedChoices = CorePlaybackOptions.PresetSpeeds;
        ThemeChoices = Enum.GetValues<ThemePreference>();

        OpenCommand = new AsyncRelayCommand(OpenAsync, CanUseFileCommands);
        SaveCommand = new AsyncRelayCommand(() => SaveCurrentAsync(saveAs: false), CanSave);
        SaveAsCommand = new AsyncRelayCommand(() => SaveCurrentAsync(saveAs: true), CanSave);
        RecordCommand = new AsyncRelayCommand(ToggleRecordingAsync, CanRecord);
        PlayCommand = new AsyncRelayCommand(PlayAsync, CanPlay);
        StopCommand = new AsyncRelayCommand(StopAsync, CanStop);
        PauseResumeCommand = new AsyncRelayCommand(PauseResumeAsync, () => _stateMachine.State is AppState.Playing or AppState.Paused);
        ExportCommand = new AsyncRelayCommand(ExportAsync, CanExport);
        ApplyHotkeysCommand = new AsyncRelayCommand(ApplyHotkeysAsync, () => !IsBusy && _initialized);
        SaveProfileCommand = new AsyncRelayCommand(SaveProfileAsync, CanSaveProfile);
        LoadProfileCommand = new AsyncRelayCommand(LoadProfileAsync, CanUseSelectedProfile);
        DeleteProfileCommand = new AsyncRelayCommand(DeleteProfileAsync, CanUseSelectedProfile);
        ToggleExpandedCommand = new RelayCommand(ToggleExpanded);
        ToggleSettingsCommand = new RelayCommand(ToggleSettings, () => !IsBusy);
        DismissErrorCommand = new RelayCommand(DismissError, () => HasError);
        UndoCommand = new RelayCommand(Undo, CanUndo);
        RedoCommand = new RelayCommand(Redo, CanRedo);
        DeleteEventCommand = new RelayCommand(DeleteSelectedEvent, CanEditSelectedEvent);
        ClearAllEventsCommand = new RelayCommand(ClearAllEvents, CanClearAllEvents);
        CreateDirectionalHoldPresetCommand = new RelayCommand(CreateDirectionalHoldPreset, CanUseFileCommands);
        MoveEventUpCommand = new RelayCommand(() => MoveSelectedEvent(-1), () => CanMoveSelectedEvent(-1));
        MoveEventDownCommand = new RelayCommand(() => MoveSelectedEvent(1), () => CanMoveSelectedEvent(1));
        AddStepCommand = new RelayCommand(AddStep, CanUseFileCommands);
        DuplicateStepCommand = new RelayCommand(DuplicateStep, () => CanUseFileCommands() && SelectedStep is not null);
        DeleteStepCommand = new RelayCommand(DeleteStep, () => CanUseFileCommands() && SelectedStep is not null);
        MoveStepUpCommand = new RelayCommand(() => MoveStep(-1), () => CanMoveStep(-1));
        MoveStepDownCommand = new RelayCommand(() => MoveStep(1), () => CanMoveStep(1));
        RecordKeybindCommand = new RelayCommand<StepRowViewModel>(row => _ = CaptureStepInputAsync(row), row => CanUseFileCommands() && row is not null);
        ClearKeybindCommand = new RelayCommand<StepRowViewModel>(row => ClearStepInputs(row), row => CanUseFileCommands() && row is not null);

        foreach (var command in new[]
                 {
                     OpenCommand, SaveCommand, SaveAsCommand, RecordCommand, PlayCommand,
                     StopCommand, PauseResumeCommand, ExportCommand, ApplyHotkeysCommand,
                     SaveProfileCommand, LoadProfileCommand, DeleteProfileCommand,
                 })
        {
            command.ExecutionFailed += OnCommandExecutionFailed;
        }

        _stateMachine.StateChanged += OnStateChanged;
        _history.Changed += (_, _) => RefreshCommands();
        _themeService.ThemeChanged += OnThemeChanged;
        _recorder.EventRecorded += OnRecorderEventRecorded;
        _recorder.EmergencyStopRequested += (_, _) => DispatchAsync(StopAsync);
        _recorder.Faulted += (_, exception) => DispatchAsync(() => HandleRuntimeFailureAsync("Recording failed", exception));
        _recorder.SensitiveInputBlocked += OnSensitiveInputBlocked;
        _playback.ProgressChanged += (_, args) => Interlocked.Exchange(ref _pendingPlaybackProgress, args);
        _playback.PlaybackCompleted += OnPlaybackCompleted;

        _uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, OnUiTimerTick, _dispatcher);
        _uiTimer.Start();
    }

    public ObservableCollection<EventRowViewModel> Events { get; }
    public ObservableCollection<StepRowViewModel> Steps { get; }
    public IReadOnlyList<MacroStepAction> StepActions { get; } = Enum.GetValues<MacroStepAction>();
    public IReadOnlyList<DurationUnit> DurationUnits { get; } = Enum.GetValues<DurationUnit>();

    public ObservableCollection<string> Profiles { get; }

    public IReadOnlyList<double> SpeedChoices { get; }

    public IReadOnlyList<ThemePreference> ThemeChoices { get; }

    public AsyncRelayCommand OpenCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand SaveAsCommand { get; }
    public AsyncRelayCommand RecordCommand { get; }
    public AsyncRelayCommand PlayCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand PauseResumeCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public AsyncRelayCommand ApplyHotkeysCommand { get; }
    public AsyncRelayCommand SaveProfileCommand { get; }
    public AsyncRelayCommand LoadProfileCommand { get; }
    public AsyncRelayCommand DeleteProfileCommand { get; }
    public RelayCommand ToggleExpandedCommand { get; }
    public RelayCommand ToggleSettingsCommand { get; }
    public RelayCommand DismissErrorCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand DeleteEventCommand { get; }
    public RelayCommand ClearAllEventsCommand { get; }
    public RelayCommand CreateDirectionalHoldPresetCommand { get; }
    public RelayCommand MoveEventUpCommand { get; }
    public RelayCommand MoveEventDownCommand { get; }
    public RelayCommand AddStepCommand { get; }
    public RelayCommand DuplicateStepCommand { get; }
    public RelayCommand DeleteStepCommand { get; }
    public RelayCommand MoveStepUpCommand { get; }
    public RelayCommand MoveStepDownCommand { get; }
    public RelayCommand<StepRowViewModel> RecordKeybindCommand { get; }
    public RelayCommand<StepRowViewModel> ClearKeybindCommand { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(WindowHeight));
                OnPropertyChanged(nameof(WindowWidth));
                OnPropertyChanged(nameof(WindowMinWidth));
                OnPropertyChanged(nameof(InspectorToggleAutomationName));
            }
        }
    }

    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        private set => SetProperty(ref _isSettingsOpen, value);
    }

    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set => SetProperty(ref _alwaysOnTop, value);
    }

    public bool CountdownEnabled
    {
        get => _countdownEnabled;
        set => SetProperty(ref _countdownEnabled, value);
    }

    public bool ContinuousPlayback
    {
        get => _continuousPlayback;
        set
        {
            if (SetProperty(ref _continuousPlayback, value))
            {
                OnPropertyChanged(nameof(RemainingText));
            }
        }
    }

    public bool LockMouseDuringDirectionalHold
    {
        get => _lockMouseDuringDirectionalHold;
        set
        {
            if (SetProperty(ref _lockMouseDuringDirectionalHold, value))
            {
                OnPropertyChanged(nameof(MouseLockStatusText));
            }
        }
    }

    public string ProfileName
    {
        get => _profileName;
        set
        {
            if (SetProperty(ref _profileName, value ?? string.Empty))
            {
                RefreshCommands();
            }
        }
    }

    public string ProfileStatusText
    {
        get => _profileStatusText;
        private set => SetProperty(ref _profileStatusText, value);
    }

    public int RepeatCount
    {
        get => _repeatCount;
        set => SetProperty(ref _repeatCount, Math.Clamp(value, CorePlaybackOptions.MinimumRepeatCount, CorePlaybackOptions.MaximumRepeatCount));
    }

    public double PlaybackSpeed
    {
        get => _playbackSpeed;
        set
        {
            if (double.IsFinite(value))
            {
                SetProperty(ref _playbackSpeed, Math.Clamp(value, CorePlaybackOptions.MinimumSpeed, CorePlaybackOptions.MaximumSpeed));
            }
        }
    }

    public ThemePreference Theme
    {
        get => _settings.Theme;
        set
        {
            if (_settings.Theme == value)
            {
                return;
            }

            _settings.Theme = value;
            _themeService.Apply(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(StateBrush));
        }
    }

    public string RecordHotkeyText
    {
        get => _recordHotkeyText;
        set => SetProperty(ref _recordHotkeyText, value);
    }

    public string PlayHotkeyText
    {
        get => _playHotkeyText;
        set => SetProperty(ref _playHotkeyText, value);
    }

    public string PauseHotkeyText { get => _pauseHotkeyText; set => SetProperty(ref _pauseHotkeyText, value); }

    public string StopHotkeyText
    {
        get => _stopHotkeyText;
        set => SetProperty(ref _stopHotkeyText, value);
    }

    public EventRowViewModel? SelectedEvent
    {
        get => _selectedEvent;
        set
        {
            if (SetProperty(ref _selectedEvent, value))
            {
                RefreshCommands();
            }
        }
    }

    public StepRowViewModel? SelectedStep
    {
        get => _selectedStep;
        set { if (SetProperty(ref _selectedStep, value)) RefreshCommands(); }
    }

    public bool IsRecording => _stateMachine.State == AppState.Recording;
    public bool IsPlaying => _stateMachine.State is AppState.Playing or AppState.Paused;
    public bool IsPaused => _stateMachine.State == AppState.Paused;
    public string PauseResumeLabel => IsPaused ? "Resume" : "Pause";
    public bool IsBusy => _stateMachine.State is AppState.Recording or AppState.Playing or AppState.Paused or AppState.Stopping || IsCountingDown;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string? ErrorMessage => _errorMessage;
    public double WindowHeight => (IsExpanded ? ExpandedHeight : CompactHeight) + (HasError ? ErrorBannerHeight : 0);
    public double WindowWidth => IsExpanded ? 1280 : 636;
    public double WindowMinWidth => IsExpanded ? 1080 : 636;
    public int EventCount => IsRecording ? Volatile.Read(ref _liveEventCount) : Events.Count;
    public string MacroDurationText => FormatDuration(GetDocumentDuration());
    public string LoopText => _loopText;
    public string RemainingText => _remainingText;
    public string DisplayLayoutStatus => _displayLayoutStatus;
    public string HotkeyStatusText => _hotkeyStatusText;
    public string CountdownActionText => _countdownActionText;
    public int CountdownValue => _countdownValue;

    public bool IsCountingDown
    {
        get => _isCountingDown;
        private set
        {
            if (SetProperty(ref _isCountingDown, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                RefreshCommands();
            }
        }
    }

    public string StatusText => _stateMachine.State switch
    {
        AppState.Recording => "Recording",
        AppState.Playing when IsDirectionalHoldPlayback => GetDirectionalHoldTimer().Phase == DirectionalHoldPhase.HoldD
            ? "D + LM1"
            : "A + LM1",
        AppState.Playing => "Playing",
        AppState.Paused => "Paused",
        AppState.Stopping => "Stopping",
        AppState.Error => "Error",
        _ when IsCountingDown => "Counting down",
        _ when _hasRunActivity => "Stopped",
        _ => "Ready",
    };

    public string ElapsedText => IsDirectionalHoldPlayback
        ? $"{FormatDirectionalHoldRemaining(GetDirectionalHoldTimer().Remaining)} left"
        : $"{FormatElapsed(_activityStopwatch.Elapsed)} · {EventCount:N0} events";

    public bool IsDirectionalHoldPlayback => IsPlaying && _activeDirectionalHoldPreset;

    public string ActiveHoldText => IsDirectionalHoldPlayback
        ? GetDirectionalHoldTimer().Phase == DirectionalHoldPhase.HoldD ? "D + LM1" : "A + LM1"
        : "—";

    public string PhaseRemainingText => IsDirectionalHoldPlayback
        ? FormatDirectionalHoldRemaining(GetDirectionalHoldTimer().Remaining)
        : "—";

    public bool IsMouseLocked => _cursorLock.IsLocked;

    public string MouseLockStatusText => IsMouseLocked && _activeMouseLockX is int x && _activeMouseLockY is int y
        ? $"Mouse locked at ({x}, {y})"
        : LockMouseDuringDirectionalHold
            ? "Mouse lock is ready for step playback."
            : "Mouse lock is off.";

    public Brush StateBrush => GetBrush(_stateMachine.State switch
    {
        AppState.Recording => "RecordBrush",
        AppState.Playing => "PlayBrush",
        AppState.Paused => "WarningBrush",
        AppState.Stopping => "WarningBrush",
        AppState.Error => "ErrorBrush",
        _ => "AccentBrush",
    });

    public string CurrentFileDisplay
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(_currentPath) ? "Unsaved macro" : Path.GetFileName(_currentPath);
            return _isDirty ? name + " *" : name;
        }
    }

    public string WindowTitle => $"{CurrentFileDisplay} - RelayLoop";

    public string RecordAutomationName => IsRecording ? "Stop recording; recording active" : "Start recording";

    public string PlayAutomationName => IsPlaying ? "Play macro; playback active" : "Play macro";

    public string InspectorToggleAutomationName => IsExpanded ? "Collapse event inspector" : "Expand event inspector";

    public async Task InitializeAsync(nint windowHandle)
    {
        if (_initialized || _disposed)
        {
            return;
        }

        try
        {
            _settings = await _settingsService.LoadAsync().ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            ApplyLoadedSettings(_settings);
            await RefreshProfileNamesAsync().ConfigureAwait(true);
            InitializeHotkeys();

            var loadedRecovery = await TryLoadRecoveryAsync().ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            if (!loadedRecovery)
            {
                await TryLoadRecentMacroAsync().ConfigureAwait(true);
            }

            await EnsureDefaultProfileAsync(
                load: _currentPath is null && _document.Events.Count == 0).ConfigureAwait(true);

            if (_disposed)
            {
                return;
            }

            _initialized = true;
            _logger?.Information("application_initialized");
            RefreshAll();
        }
        catch (Exception exception)
        {
            _initialized = true;
            EnterError("RelayLoop could not finish initialization. " + exception.Message, exception);
        }
    }

    public bool TryGetWindowPosition(out double left, out double top)
    {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        if (_windowLeft is double savedLeft && _windowTop is double savedTop &&
            double.IsFinite(savedLeft) && double.IsFinite(savedTop) &&
            savedLeft < virtualRight - 48 && savedLeft + 160 > virtualLeft &&
            savedTop < virtualBottom - 32 && savedTop + CompactHeight > virtualTop)
        {
            left = Math.Clamp(savedLeft, virtualLeft, Math.Max(virtualLeft, virtualRight - 160));
            top = Math.Clamp(savedTop, virtualTop, Math.Max(virtualTop, virtualBottom - CompactHeight));
            return true;
        }

        left = SystemParameters.WorkArea.Left + 24;
        top = SystemParameters.WorkArea.Top + 24;
        return false;
    }

    public void UpdateWindowPosition(double left, double top)
    {
        if (double.IsFinite(left) && double.IsFinite(top))
        {
            _windowLeft = left;
            _windowTop = top;
        }
    }

    public async Task OpenPathAsync(string path)
    {
        if (_disposed || IsBusy)
        {
            SetBanner("Stop recording or playback before opening a macro.");
            return;
        }

        if (!await ConfirmSafeToReplaceDocumentAsync().ConfigureAwait(true))
        {
            return;
        }

        try
        {
            var document = await MacroSerializer.LoadAsync(path).ConfigureAwait(true);
            AdoptDocument(document, Path.GetFullPath(path), isDirty: false);
            _settings.RecentMacroPath = _currentPath;
            ClearBanner();
            _logger?.Information("macro_opened");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or MacroValidationException or MacroFormatException)
        {
            EnterError("The macro could not be opened. " + exception.Message, exception);
        }
    }

    public async Task<bool> RequestCloseAsync()
    {
        if (_disposed)
        {
            return true;
        }

        if (_closeInProgress)
        {
            return false;
        }

        _closeInProgress = true;
        try
        {
            _countdownCancellation?.Cancel();
            if (IsRecording || IsPlaying || _stateMachine.State == AppState.Stopping)
            {
                await StopAsync().ConfigureAwait(true);
            }

            try
            {
                // Also retries any release packet that Windows rejected during a prior playback
                // failure, including when the state machine is already showing Error.
                await _playback.StopAsync().ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                _dialogs.ShowError(
                    "RelayLoop input release warning",
                    "Windows continued to reject one or more held-input release events. " +
                    "Press and release any affected keys or mouse buttons manually after RelayLoop closes.\n\n" +
                    exception.Message);
                _logger?.Error("shutdown_input_release_failed", exception);
            }

            if (!await ConfirmSafeToReplaceDocumentAsync().ConfigureAwait(true))
            {
                return false;
            }

            _settings = CreateSettingsSnapshot();
            try
            {
                await _settingsService.SaveAsync(_settings).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                _dialogs.ShowError("RelayLoop settings", "Settings could not be saved. " + exception.Message);
            }

            await DisposeAsync().ConfigureAwait(true);
            return true;
        }
        finally
        {
            if (!_disposed)
            {
                _closeInProgress = false;
            }
        }
    }

    public void EmergencyShutdown()
    {
        lock (_shutdownGate)
        {
            if (_disposed)
            {
                return;
            }

            _countdownCancellation?.Cancel();
            try
            {
                _recorder.Stop();
            }
            catch
            {
                // Continue to playback cleanup even if hook teardown reports a failure.
            }

            try
            {
                _playback.StopAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // InputPlaybackService performs held-input release in its own finally block.
            }

            DisposeServicesSynchronously();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_shutdownGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _uiTimer.Stop();
        _countdownCancellation?.Cancel();
        DisposeHotkeys();
        Exception? disposalFailure = null;
        try
        {
            _recorder.Dispose();
        }
        catch (Exception exception)
        {
            disposalFailure = exception;
        }

        try { _inputCapture.Dispose(); }
        catch (Exception exception) { disposalFailure = disposalFailure is null ? exception : new AggregateException(disposalFailure, exception); }

        try
        {
            await _playback.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            disposalFailure = disposalFailure is null
                ? exception
                : new AggregateException(disposalFailure, exception);
        }

        try
        {
            _cursorLock.Dispose();
        }
        catch (Exception exception)
        {
            disposalFailure = disposalFailure is null
                ? exception
                : new AggregateException(disposalFailure, exception);
        }
        finally
        {
            _recoveryWriteGate.Dispose();
            _themeService.ThemeChanged -= OnThemeChanged;
            _themeService.Dispose();
        }

        GC.SuppressFinalize(this);
        if (disposalFailure is not null)
        {
            ExceptionDispatchInfo.Capture(disposalFailure).Throw();
        }
    }

    private async Task OpenAsync()
    {
        var path = _dialogs.ChooseMacroToOpen(GetCurrentDirectory());
        if (!string.IsNullOrWhiteSpace(path))
        {
            await OpenPathAsync(path).ConfigureAwait(true);
        }
    }

    private async Task<bool> SaveCurrentAsync(bool saveAs)
    {
        if (_disposed || IsBusy)
        {
            return false;
        }

        var destination = saveAs ? null : _currentPath;
        if (string.IsNullOrWhiteSpace(destination))
        {
            destination = _dialogs.ChooseMacroToSave(GetSuggestedMacroName(), GetCurrentDirectory());
            if (string.IsNullOrWhiteSpace(destination))
            {
                return false;
            }
        }

        try
        {
            var snapshot = CreateDocumentSnapshot(ensureDisplayLayout: true);
            await MacroSerializer.SaveAsync(destination, snapshot).ConfigureAwait(true);
            _document = snapshot;
            _currentPath = Path.GetFullPath(destination);
            _settings.RecentMacroPath = _currentPath;
            _isDirty = false;
            _recovery.Clear();
            ClearBanner();
            _history.Reset(_document);
            _logger?.Information("macro_saved");
            RefreshDocumentProperties();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or MacroValidationException)
        {
            EnterError("The macro could not be saved. " + exception.Message, exception);
            return false;
        }
    }

    private async Task SaveProfileAsync()
    {
        string name;
        try
        {
            name = ProfileService.ValidateName(ProfileName);
        }
        catch (ArgumentException exception)
        {
            SetBanner(exception.Message);
            return;
        }

        try
        {
            if (await _profileService.ExistsAsync(name).ConfigureAwait(true) &&
                !_dialogs.ConfirmOverwriteProfile(name))
            {
                return;
            }

            var profile = new MacroProfile
            {
                Name = name,
                Document = CreateDocumentSnapshot(ensureDisplayLayout: true),
                PlaybackSpeed = PlaybackSpeed,
                RepeatCount = RepeatCount,
                ContinuousPlayback = ContinuousPlayback,
                LockMouseDuringDirectionalHold = LockMouseDuringDirectionalHold,
            };
            await _profileService.SaveAsync(profile).ConfigureAwait(true);
            ProfileName = name;
            await RefreshProfileNamesAsync().ConfigureAwait(true);
            ClearBanner();
            ProfileStatusText = $"Saved profile ‘{name}’.";
            _logger?.Information("profile_saved");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or MacroValidationException or ArgumentOutOfRangeException)
        {
            EnterError("The profile could not be saved. " + exception.Message, exception);
        }
    }

    private async Task LoadProfileAsync()
    {
        var name = FindProfileName(ProfileName);
        if (name is null || !await ConfirmSafeToReplaceDocumentAsync().ConfigureAwait(true))
        {
            return;
        }

        try
        {
            var profile = await _profileService.LoadAsync(name).ConfigureAwait(true);
            AdoptDocument(profile.Document, path: null, isDirty: true);
            PlaybackSpeed = profile.PlaybackSpeed;
            RepeatCount = profile.RepeatCount;
            ContinuousPlayback = profile.ContinuousPlayback;
            LockMouseDuringDirectionalHold = profile.LockMouseDuringDirectionalHold;
            ProfileName = profile.Name;
            ClearBanner();
            ProfileStatusText = $"Loaded profile ‘{profile.Name}’.";
            _logger?.Information("profile_loaded");
            RefreshAll();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or InvalidDataException or JsonException or MacroValidationException or ArgumentOutOfRangeException)
        {
            EnterError("The profile could not be loaded. " + exception.Message, exception);
            await RefreshProfileNamesAsync().ConfigureAwait(true);
        }
    }

    private async Task DeleteProfileAsync()
    {
        var name = FindProfileName(ProfileName);
        if (name is null || !_dialogs.ConfirmDeleteProfile(name))
        {
            return;
        }

        try
        {
            await _profileService.DeleteAsync(name).ConfigureAwait(true);
            ProfileName = string.Empty;
            await RefreshProfileNamesAsync().ConfigureAwait(true);
            ClearBanner();
            ProfileStatusText = $"Deleted profile ‘{name}’.";
            _logger?.Information("profile_deleted");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            EnterError("The profile could not be deleted. " + exception.Message, exception);
        }
    }

    private async Task RefreshProfileNamesAsync()
    {
        try
        {
            var selected = ProfileName;
            var names = await _profileService.ListNamesAsync().ConfigureAwait(true);
            Profiles.Clear();
            foreach (var name in names)
            {
                Profiles.Add(name);
            }

            ProfileName = FindProfileName(selected) ?? selected;
            OnPropertyChanged(nameof(Profiles));
            RefreshCommands();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            ProfileStatusText = "Saved profiles are unavailable: " + exception.Message;
            _logger?.Warning("profile_list_failed");
        }
    }

    private async Task EnsureDefaultProfileAsync(bool load)
    {
        const string name = "Default D-A Hold";
        MacroProfile profile;
        if (await _profileService.ExistsAsync(name).ConfigureAwait(true))
        {
            profile = await _profileService.LoadAsync(name).ConfigureAwait(true);
        }
        else
        {
            var document = new MacroDocument
            {
                DisplayLayout = _displayLayouts.Capture(),
                Steps = MacroStepCompiler.CreateDefault(),
            };
            document.Events = MacroStepCompiler.Compile(document.Steps);
            profile = new MacroProfile { Name = name, Document = document, ContinuousPlayback = true, RepeatCount = 1 };
            await _profileService.SaveAsync(profile).ConfigureAwait(true);
            await RefreshProfileNamesAsync().ConfigureAwait(true);
        }
        if (load)
        {
            AdoptDocument(profile.Document, null, isDirty: false);
            ContinuousPlayback = profile.ContinuousPlayback;
            RepeatCount = profile.RepeatCount;
            ProfileName = name;
            ProfileStatusText = "Loaded the editable default D/A profile.";
        }
    }

    private async Task ToggleRecordingAsync()
    {
        if (IsRecording)
        {
            await StopAsync().ConfigureAwait(true);
            return;
        }

        if (!await ConfirmSafeToReplaceDocumentAsync().ConfigureAwait(true))
        {
            return;
        }

        if (!await RunCountdownAsync("Recording starts in").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            _recordingDisplayLayout = _displayLayouts.Capture();
            if (!_stateMachine.TryBeginRecording(out var reason))
            {
                SetBanner(reason ?? "Recording cannot start in the current state.");
                return;
            }

            while (_recordedEventQueue.TryDequeue(out _)) { }
            Volatile.Write(ref _liveEventCount, 0);
            _document = new MacroDocument
            {
                CreatedUtc = DateTimeOffset.UtcNow,
                DisplayLayout = _recordingDisplayLayout.DeepClone(),
                Events = [],
            };
            RebuildRows(_document);
            _currentPath = null;
            _isDirty = true;
            _hasRunActivity = true;
            _lastRecoveryWrite = DateTimeOffset.MinValue;
            _activityStopwatch.Restart();
            _loopText = "—";
            _remainingText = "—";
            _recorder.ConfigureControlGestures(_recordGesture, _playGesture, _pauseGesture, _stopGesture);
            await Task.Run(_recorder.Start).ConfigureAwait(true);
            ClearBanner();
            _logger?.Information("recording_started");
            RefreshAll();
        }
        catch (Exception exception)
        {
            _activityStopwatch.Stop();
            EnterError("Recording could not start. " + exception.Message, exception);
        }
    }

    private async Task PlayAsync()
    {
        if (_stopRegistration is null)
        {
            EnterError("Playback is disabled because the emergency-stop hotkey is not registered. Open Settings and apply an available stop hotkey.");
            return;
        }

        MacroDocument snapshot;
        try
        {
            snapshot = CreateDocumentSnapshot(ensureDisplayLayout: true);
            MacroValidator.Validate(snapshot);
            if (!snapshot.Events.Any(static item => item.Enabled))
            {
                SetBanner("There are no enabled events to play.");
                return;
            }
        }
        catch (Exception exception) when (exception is MacroValidationException or InvalidOperationException)
        {
            EnterError("The macro is not ready for playback. " + exception.Message, exception);
            return;
        }

        if (snapshot.DisplayLayout is not null)
        {
            try
            {
                var comparison = _displayLayouts.CompareWithCurrent(snapshot.DisplayLayout);
                if (!comparison.IsEquivalent && !_dialogs.ConfirmLayoutMismatch(BuildLayoutWarning(comparison)))
                {
                    return;
                }
            }
            catch (Exception exception)
            {
                EnterError("The current display layout could not be verified. " + exception.Message, exception);
                return;
            }
        }

        if (!await RunCountdownAsync("Playback starts in").ConfigureAwait(true))
        {
            return;
        }

        if (!_stateMachine.TryBeginPlayback(out var reason))
        {
            SetBanner(reason ?? "Playback cannot start in the current state.");
            return;
        }

        _activeDirectionalHoldPreset = DirectionalHoldPreset.IsMatch(snapshot.Events);
        _activePlaybackSpeed = PlaybackSpeed;
        _hasRunActivity = true;
        _activityStopwatch.Restart();
        _loopText = ContinuousPlayback ? "1 / ∞" : $"1 / {RepeatCount:N0}";
        _remainingText = ContinuousPlayback
            ? "Until stopped"
            : FormatDuration(PlaybackPlanner.GetTotalDuration(snapshot, new RelayLoop.Core.PlaybackOptions(_activePlaybackSpeed, RepeatCount, false)) ?? TimeSpan.Zero);
        ClearBanner();
        RefreshAll();

        try
        {
            if (LockMouseDuringDirectionalHold)
            {
                var stepTarget = snapshot.Steps?.FirstOrDefault(step => step.Inputs.Any(input => input.Kind == MacroInputKind.MouseButton));
                var legacyTarget = snapshot.Events.FirstOrDefault(item => item.Kind is MacroEventKind.MouseButtonDown or MacroEventKind.MouseButtonUp);
                var targetX = stepTarget?.MouseX ?? legacyTarget?.X;
                var targetY = stepTarget?.MouseY ?? legacyTarget?.Y;
                if (targetX is null || targetY is null) throw new InvalidOperationException("Mouse lock requires a step containing a mouse button.");
                _cursorLock.LockAt(targetX.Value, targetY.Value);
                _activeMouseLockX = targetX;
                _activeMouseLockY = targetY;
                RefreshMouseLockProperties();
                _logger?.Information("directional_hold_mouse_locked");
            }

            await _playback.PlayAsync(snapshot.Events, new ServicePlaybackOptions
            {
                Speed = _activePlaybackSpeed,
                RepeatCount = RepeatCount,
                Continuous = ContinuousPlayback,
            }).ConfigureAwait(true);
            _logger?.Information("playback_completed");
        }
        catch (OperationCanceledException)
        {
            _logger?.Information("playback_cancelled");
        }
        catch (Exception exception)
        {
            EnterError("Playback stopped because Windows could not control or safely release an input. " + exception.Message, exception);
        }
        finally
        {
            _activityStopwatch.Stop();
            try
            {
                _cursorLock.Release();
            }
            catch (Win32Exception exception)
            {
                EnterError("Windows could not release the mouse-position lock. " + exception.Message, exception);
            }

            _activeMouseLockX = null;
            _activeMouseLockY = null;
            _activeDirectionalHoldPreset = false;
            RefreshMouseLockProperties();
            if (_stateMachine.State is AppState.Playing or AppState.Paused)
            {
                _stateMachine.TryRequestStop(out _);
            }

            if (_stateMachine.State == AppState.Stopping)
            {
                _stateMachine.TryCompleteStop(out _);
            }

            _remainingText = "—";
            RefreshAll();
        }
    }

    private async Task PauseResumeAsync()
    {
        if (_stateMachine.State == AppState.Playing)
        {
            await _playback.PauseAsync().ConfigureAwait(true);
            _cursorLock.Release();
            _activityStopwatch.Stop();
            _stateMachine.TryPause(out _);
        }
        else if (_stateMachine.State == AppState.Paused)
        {
            if (_activeMouseLockX is int x && _activeMouseLockY is int y && LockMouseDuringDirectionalHold) _cursorLock.LockAt(x, y);
            _playback.Resume();
            _activityStopwatch.Start();
            _stateMachine.TryResume(out _);
        }
        RefreshAll();
    }

    private async Task StopAsync()
    {
        _countdownCancellation?.Cancel();
        if (IsCountingDown)
        {
            return;
        }

        var state = _stateMachine.State;
        if (state == AppState.Recording)
        {
            _stateMachine.TryRequestStop(out _);
            try
            {
                var captured = await Task.Run(_recorder.Stop).ConfigureAwait(true);
                _activityStopwatch.Stop();
                _document = new MacroDocument
                {
                    CreatedUtc = _document.CreatedUtc,
                    DisplayLayout = _recordingDisplayLayout?.DeepClone() ?? _displayLayouts.Capture(),
                    Events = captured.Select(static item => item.DeepClone()).ToList(),
                };
                Volatile.Write(ref _liveEventCount, _document.Events.Count);
                RebuildRows(_document);
                _history.Reset(_document);
                _isDirty = true;
                await SaveRecoverySnapshotAsync(_document).ConfigureAwait(true);
                _logger?.Information("recording_stopped");
            }
            catch (Exception exception)
            {
                EnterError("Recording could not be stopped cleanly. " + exception.Message, exception);
                return;
            }

            if (_stateMachine.State == AppState.Stopping)
            {
                _stateMachine.TryCompleteStop(out _);
            }
        }
        else if (state is AppState.Playing or AppState.Paused or AppState.Stopping)
        {
            if (state is AppState.Playing or AppState.Paused)
            {
                _stateMachine.TryRequestStop(out _);
            }

            try
            {
                await _playback.StopAsync().ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                EnterError("Playback cleanup failed. " + exception.Message, exception);
                return;
            }

            if (_stateMachine.State == AppState.Stopping)
            {
                _stateMachine.TryCompleteStop(out _);
            }
        }

        _activityStopwatch.Stop();
        _hasRunActivity = true;
        _remainingText = "—";
        RefreshAll();
    }

    private async Task ExportAsync()
    {
        string suggested = Path.GetFileNameWithoutExtension(_currentPath) ?? "RelayLoop macro";
        var destination = _dialogs.ChooseRunnerDestination(suggested, GetCurrentDirectory());
        if (string.IsNullOrWhiteSpace(destination))
        {
            return;
        }

        try
        {
            var snapshot = CreateDocumentSnapshot(ensureDisplayLayout: true);
            var result = await _runnerExporter.ExportAsync(snapshot, destination).ConfigureAwait(true);
            if (!result.Success)
            {
                EnterError(result.ErrorMessage ?? "The standalone runner could not be exported.");
                return;
            }

            _dialogs.ShowInformation(
                "RelayLoop runner exported",
                $"The portable runner was created at:\n{result.OutputPath}\n\nIt will show a confirmation screen before sending any input.");
            _logger?.Information("runner_exported");
        }
        catch (Exception exception)
        {
            EnterError("The standalone runner could not be exported. " + exception.Message, exception);
        }
    }

    private async Task ApplyHotkeysAsync()
    {
        try
        {
            var record = HotKeyParser.Parse(RecordHotkeyText);
            var play = HotKeyParser.Parse(PlayHotkeyText);
            var pause = HotKeyParser.Parse(PauseHotkeyText);
            var stop = HotKeyParser.Parse(StopHotkeyText);
            EnsureDistinctHotkeys(record, play, pause, stop);

            var previous = (_recordGesture, _playGesture, _pauseGesture, _stopGesture);
            DisposeHotkeyRegistrations(throwOnFailure: true);
            try
            {
                RegisterAllHotkeys(record, play, pause, stop);
                _recordGesture = record;
                _playGesture = play;
                _pauseGesture = pause;
                _stopGesture = stop;
                _recorder.ConfigureControlGestures(record, play, pause, stop);
                RecordHotkeyText = record.ToString();
                PlayHotkeyText = play.ToString();
                PauseHotkeyText = pause.ToString();
                StopHotkeyText = stop.ToString();
                _hotkeyStatusText = "Global hotkeys are active.";
                ClearBanner();
            }
            catch (Exception registrationException)
            {
                DisposeHotkeyRegistrations(throwOnFailure: false);
                try
                {
                    RegisterAllHotkeys(previous._recordGesture, previous._playGesture, previous._pauseGesture, previous._stopGesture);
                    _recordGesture = previous._recordGesture;
                    _playGesture = previous._playGesture;
                    _pauseGesture = previous._pauseGesture;
                    _stopGesture = previous._stopGesture;
                    RecordHotkeyText = _recordGesture.ToString();
                    PlayHotkeyText = _playGesture.ToString();
                    PauseHotkeyText = _pauseGesture.ToString();
                    StopHotkeyText = _stopGesture.ToString();
                    _hotkeyStatusText = "The previous global hotkeys remain active.";
                }
                catch (Exception rollbackException)
                {
                    _hotkeyStatusText = "Global hotkeys are unavailable. Playback is disabled until an emergency-stop hotkey is registered.";
                    _logger?.Error("hotkey_rollback_failed", rollbackException);
                }

                throw new InvalidOperationException(registrationException.Message, registrationException);
            }

            _settings.RecordHotkey = ToSetting(_recordGesture);
            _settings.PlayHotkey = ToSetting(_playGesture);
            _settings.PauseHotkey = ToSetting(_pauseGesture);
            _settings.StopHotkey = ToSetting(_stopGesture);
            await _settingsService.SaveAsync(CreateSettingsSnapshot()).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or HotKeyRegistrationException or AggregateException or IOException or UnauthorizedAccessException or SecurityException)
        {
            SetBanner("Hotkeys were not changed. " + exception.Message);
            _logger?.Warning("hotkey_apply_failed");
        }
        finally
        {
            OnPropertyChanged(nameof(HotkeyStatusText));
            RefreshCommands();
        }
    }

    private void Undo()
    {
        if (_history.TryUndo(out var document))
        {
            _document = document;
            RebuildRows(document);
            _isDirty = true;
            RefreshDocumentProperties();
        }
    }

    private void Redo()
    {
        if (_history.TryRedo(out var document))
        {
            _document = document;
            RebuildRows(document);
            _isDirty = true;
            RefreshDocumentProperties();
        }
    }

    private void DeleteSelectedEvent()
    {
        if (SelectedEvent is null)
        {
            return;
        }

        var index = Events.IndexOf(SelectedEvent);
        if (index < 0)
        {
            return;
        }

        _document = CreateDocumentSnapshot();
        _document.Steps = null;
        _document.Events.RemoveAt(index);
        _history.Push(_document);
        RebuildRows(_document);
        SelectedEvent = index < Events.Count ? Events[index] : Events.LastOrDefault();
        MarkEdited();
    }

    private void ClearAllEvents()
    {
        if (Events.Count == 0 || !_dialogs.ConfirmClearAllEvents(Events.Count))
        {
            return;
        }

        _document = CreateDocumentSnapshot();
        _document.Steps = null;
        _document.Events.Clear();
        _history.Push(_document);
        RebuildRows(_document);
        SelectedEvent = null;
        MarkEdited();
    }

    private void CreateDirectionalHoldPreset()
    {
        var target = SelectedEvent is not null && IsMousePositionEvent(SelectedEvent.Kind)
            ? SelectedEvent
            : Events.LastOrDefault(static item => IsMousePositionEvent(item.Kind));
        if (target is null)
        {
            _dialogs.ShowInformation(
                "RelayLoop - choose an LM1 target",
                "Record or select a mouse event first. RelayLoop uses that event's screen position for the two-minute LM1 holds.");
            return;
        }

        if (Events.Count > 0 && !_dialogs.ConfirmReplaceWithDirectionalHoldPreset(Events.Count))
        {
            return;
        }

        _document = CreateDocumentSnapshot();
        _document.Steps = MacroStepCompiler.CreateDefault(target.X, target.Y);
        _document.Events = MacroStepCompiler.Compile(_document.Steps);
        _history.Push(_document);
        RebuildRows(_document);
        SelectedEvent = Events.FirstOrDefault();
        PlaybackSpeed = 1;
        RepeatCount = 1;
        ContinuousPlayback = true;
        ClearBanner();
        MarkEdited();
    }

    private static bool IsMousePositionEvent(MacroEventKind kind) => kind is
        MacroEventKind.MouseMove or
        MacroEventKind.MouseButtonDown or
        MacroEventKind.MouseButtonUp or
        MacroEventKind.MouseWheel;

    private void MoveSelectedEvent(int offset)
    {
        if (SelectedEvent is null)
        {
            return;
        }

        var index = Events.IndexOf(SelectedEvent);
        var destination = index + offset;
        if (index < 0 || destination < 0 || destination >= Events.Count)
        {
            return;
        }

        _document = CreateDocumentSnapshot();
        _document.Steps = null;
        (_document.Events[index], _document.Events[destination]) = (_document.Events[destination], _document.Events[index]);
        _history.Push(_document);
        RebuildRows(_document);
        SelectedEvent = Events[destination];
        MarkEdited();
    }

    private void OnEventRowChanged(EventRowViewModel row, MacroEvent before, MacroEvent after)
    {
        if (_updatingRows || IsBusy)
        {
            return;
        }

        var index = Events.IndexOf(row);
        if (index < 0)
        {
            return;
        }

        _document = CreateDocumentSnapshot();
        _document.Steps = null;
        _document.Events[index] = after.DeepClone();
        _history.Push(_document);
        MarkEdited();
    }

    private void MarkEdited()
    {
        _isDirty = true;
        RefreshDocumentProperties();
    }

    private void AddStep()
    {
        var step = new MacroStepDefinition { Action = MacroStepAction.Hold, Duration = 1, DurationUnit = DurationUnit.Seconds };
        var model = Steps.Select(static row => row.ToModel()).ToList();
        model.Add(step);
        ApplySteps(model, model.Count - 1);
    }

    private void DuplicateStep()
    {
        if (SelectedStep is null) return;
        var model = Steps.Select(static row => row.ToModel()).ToList();
        var index = Steps.IndexOf(SelectedStep);
        var copy = SelectedStep.ToModel();
        copy.Id = Guid.NewGuid();
        model.Insert(index + 1, copy);
        ApplySteps(model, index + 1);
    }

    private void DeleteStep()
    {
        if (SelectedStep is null) return;
        var model = Steps.Select(static row => row.ToModel()).ToList();
        var index = Steps.IndexOf(SelectedStep);
        model.RemoveAt(index);
        ApplySteps(model, Math.Min(index, model.Count - 1));
    }

    private void MoveStep(int offset)
    {
        if (SelectedStep is null) return;
        var model = Steps.Select(static row => row.ToModel()).ToList();
        var index = Steps.IndexOf(SelectedStep);
        var destination = index + offset;
        if (destination < 0 || destination >= model.Count) return;
        (model[index], model[destination]) = (model[destination], model[index]);
        ApplySteps(model, destination);
    }

    private bool CanMoveStep(int offset)
    {
        if (!CanUseFileCommands() || SelectedStep is null) return false;
        var destination = Steps.IndexOf(SelectedStep) + offset;
        return destination >= 0 && destination < Steps.Count;
    }

    private async Task CaptureStepInputAsync(StepRowViewModel? row)
    {
        if (row is null || IsBusy) return;
        row.IsCapturing = true;
        try
        {
            var inputs = await _inputCapture.CaptureAsync().ConfigureAwait(true);
            row.ReplaceInputs(inputs);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { SetBanner("Keybind capture failed. " + exception.Message); }
        finally { row.IsCapturing = false; }
    }

    private void ClearStepInputs(StepRowViewModel? row) => row?.ClearInputs();

    private void OnStepRowChanged(StepRowViewModel row)
    {
        if (_updatingRows || IsBusy) return;
        var id = row.ToModel().Id;
        var model = Steps.Select(static item => item.ToModel()).ToList();
        ApplySteps(model, model.FindIndex(step => step.Id == id));
    }

    private void ApplySteps(List<MacroStepDefinition> model, int selectedIndex)
    {
        _document = _document.DeepClone();
        _document.Steps = model;
        try
        {
            _document.Events = model.Count == 0 ? [] : MacroStepCompiler.Compile(model);
            ClearBanner();
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            _document.Events = [];
            SetBanner("Finish configuring the step before playback: " + exception.Message);
        }
        _history.Push(_document);
        RebuildRows(_document);
        SelectedStep = selectedIndex >= 0 && selectedIndex < Steps.Count ? Steps[selectedIndex] : null;
        MarkEdited();
    }

    private async Task<bool> ConfirmSafeToReplaceDocumentAsync()
    {
        if (!_isDirty)
        {
            return true;
        }

        var choice = _dialogs.ConfirmUnsavedChanges(Path.GetFileName(_currentPath) ?? "this macro");
        switch (choice)
        {
            case SaveChangesChoice.Save:
                return await SaveCurrentAsync(saveAs: false).ConfigureAwait(true);
            case SaveChangesChoice.Discard:
                _isDirty = false;
                _recovery.Clear();
                RefreshDocumentProperties();
                return true;
            default:
                return false;
        }
    }

    private async Task<bool> RunCountdownAsync(string actionText)
    {
        if (!CountdownEnabled)
        {
            return true;
        }

        _countdownCancellation?.Cancel();
        _countdownCancellation?.Dispose();
        _countdownCancellation = new CancellationTokenSource();
        var token = _countdownCancellation.Token;
        _countdownActionText = actionText;
        IsCountingDown = true;
        OnPropertyChanged(nameof(CountdownActionText));
        OnPropertyChanged(nameof(StatusText));

        try
        {
            for (var value = 3; value >= 1; value--)
            {
                _countdownValue = value;
                OnPropertyChanged(nameof(CountdownValue));
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(true);
            }

            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            IsCountingDown = false;
            _countdownValue = 0;
            OnPropertyChanged(nameof(CountdownValue));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    private void ApplyLoadedSettings(AppSettings settings)
    {
        _alwaysOnTop = settings.AlwaysOnTop;
        _isExpanded = settings.IsExpanded;
        _countdownEnabled = settings.CountdownEnabled;
        _playbackSpeed = settings.PlaybackSpeed;
        _repeatCount = settings.RepeatCount;
        _continuousPlayback = settings.ContinuousPlayback;
        _lockMouseDuringDirectionalHold = settings.LockMouseDuringDirectionalHold;
        _windowLeft = settings.WindowLeft;
        _windowTop = settings.WindowTop;
        _recordGesture = FromSetting(settings.RecordHotkey, HotKeyGesture.RecordDefault);
        _playGesture = FromSetting(settings.PlayHotkey, HotKeyGesture.PlayDefault);
        _pauseGesture = FromSetting(settings.PauseHotkey, HotKeyGesture.PauseDefault);
        _stopGesture = FromSetting(settings.StopHotkey, HotKeyGesture.EmergencyStopDefault);
        _recordHotkeyText = _recordGesture.ToString();
        _playHotkeyText = _playGesture.ToString();
        _pauseHotkeyText = _pauseGesture.ToString();
        _stopHotkeyText = _stopGesture.ToString();
        _themeService.Apply(settings.Theme);
    }

    private void InitializeHotkeys()
    {
        try
        {
            _hotKeyService = new GlobalHotKeyService();
            _hotKeyService.HotKeyPressed += OnHotKeyPressed;
            RegisterAllHotkeys(_recordGesture, _playGesture, _pauseGesture, _stopGesture);
            _recorder.ConfigureControlGestures(_recordGesture, _playGesture, _pauseGesture, _stopGesture);
            _hotkeyStatusText = "Global hotkeys are active.";
        }
        catch (Exception exception)
        {
            DisposeHotkeyRegistrations(throwOnFailure: false);
            _hotkeyStatusText = "One or more global hotkeys are unavailable. Playback remains disabled unless the emergency-stop hotkey is active.";
            SetBanner(exception.Message);
            _logger?.Warning("hotkey_initialization_failed");

            // Preserve the strongest safety property even if a non-stop shortcut conflicts.
            if (_hotKeyService is not null && _stopRegistration is null)
            {
                try
                {
                    _stopRegistration = _hotKeyService.Register("stop", _stopGesture);
                    _hotkeyStatusText = "Emergency stop is active; another configured hotkey conflicts.";
                }
                catch (Exception stopException)
                {
                    _logger?.Error("emergency_hotkey_unavailable", stopException);
                }
            }
        }
    }

    private void RegisterAllHotkeys(HotKeyGesture record, HotKeyGesture play, HotKeyGesture pause, HotKeyGesture stop)
    {
        if (_hotKeyService is null)
        {
            throw new InvalidOperationException("The global-hotkey service is unavailable.");
        }

        EnsureDistinctHotkeys(record, play, pause, stop);
        _stopRegistration = _hotKeyService.Register("stop", stop);
        try
        {
            _recordRegistration = _hotKeyService.Register("record", record);
            _playRegistration = _hotKeyService.Register("play", play);
            _pauseRegistration = _hotKeyService.Register("pause", pause);
        }
        catch
        {
            DisposeHotkeyRegistrations(throwOnFailure: false);
            throw;
        }
    }

    private void OnHotKeyPressed(object? sender, HotKeyPressedEventArgs args)
    {
        DispatchAsync(() => HandleHotKeyAsync(args.Name));
    }

    private async Task HandleHotKeyAsync(string name)
    {
        if (name == "stop")
        {
            await StopAsync().ConfigureAwait(true);
            return;
        }

        if (name == "record")
        {
            if (CanRecord())
            {
                await ToggleRecordingAsync().ConfigureAwait(true);
            }
            else
            {
                SetBanner("Recording cannot start while another operation is active.");
            }

            return;
        }

        if (name == "pause")
        {
            if (_stateMachine.State is AppState.Playing or AppState.Paused) await PauseResumeAsync().ConfigureAwait(true);
            return;
        }

        if (name == "play")
        {
            if (CanPlay())
            {
                await PlayAsync().ConfigureAwait(true);
            }
            else
            {
                SetBanner("Playback cannot start until recording or the current operation has stopped.");
            }
        }
    }

    private async Task<bool> TryLoadRecoveryAsync()
    {
        if (!_recovery.Exists || _recovery.LastWriteTime is not DateTime lastWrite)
        {
            return false;
        }

        if (!_dialogs.ConfirmRecovery(lastWrite))
        {
            _recovery.Clear();
            _logger?.Information("recovery_discarded");
            return false;
        }

        try
        {
            var document = await _recovery.LoadAsync().ConfigureAwait(true);
            AdoptDocument(document, path: null, isDirty: true);
            SetBanner("A recoverable recording was loaded. Save it to keep it permanently.");
            _logger?.Information("recovery_loaded");
            return true;
        }
        catch (Exception exception)
        {
            _recovery.Clear();
            SetBanner("The recovery file was invalid and was ignored. " + exception.Message);
            _logger?.Warning("recovery_load_failed");
            return false;
        }
    }

    private async Task TryLoadRecentMacroAsync()
    {
        var recent = _settings.RecentMacroPath;
        if (string.IsNullOrWhiteSpace(recent) || !File.Exists(recent))
        {
            return;
        }

        var recentPath = recent;

        try
        {
            var document = await MacroSerializer.LoadAsync(recentPath).ConfigureAwait(true);
            AdoptDocument(document, recentPath, isDirty: false);
            _logger?.Information("recent_macro_loaded");
        }
        catch (Exception exception)
        {
            _settings.RecentMacroPath = null;
            SetBanner("The most recent macro could not be loaded. " + exception.Message);
            _logger?.Warning("recent_macro_load_failed");
        }
    }

    private void AdoptDocument(MacroDocument document, string? path, bool isDirty)
    {
        MacroValidator.Validate(document);
        _document = document.DeepClone();
        _currentPath = path is null ? null : Path.GetFullPath(path);
        _isDirty = isDirty;
        _history.Reset(_document);
        RebuildRows(_document);
        UpdateDisplayLayoutStatus();
        RefreshDocumentProperties();
    }

    private MacroDocument CreateDocumentSnapshot(bool ensureDisplayLayout = false)
    {
        var snapshot = _document.DeepClone();
        snapshot.Steps = Steps.Count == 0 ? snapshot.Steps : Steps.Select(static row => row.ToModel()).ToList();
        snapshot.Events = snapshot.Steps is { Count: > 0 }
            ? MacroStepCompiler.Compile(snapshot.Steps)
            : Events.Select(static row => row.ToModel()).ToList();
        if (ensureDisplayLayout && snapshot.DisplayLayout is null)
        {
            snapshot.DisplayLayout = _displayLayouts.Capture();
        }

        return snapshot;
    }

    private void RebuildRows(MacroDocument document)
    {
        _updatingRows = true;
        try
        {
            Events.Clear();
            for (var index = 0; index < document.Events.Count; index++)
            {
                Events.Add(new EventRowViewModel(document.Events[index].DeepClone(), index + 1, OnEventRowChanged));
            }
            Steps.Clear();
            if (document.Steps is not null)
            {
                for (var index = 0; index < document.Steps.Count; index++)
                    Steps.Add(new StepRowViewModel(document.Steps[index], index + 1, OnStepRowChanged));
            }
        }
        finally
        {
            _updatingRows = false;
        }

        RefreshRowIndexes();
    }

    private void RefreshRowIndexes()
    {
        for (var index = 0; index < Events.Count; index++)
        {
            Events[index].Index = index + 1;
        }
    }

    private void OnRecorderEventRecorded(object? sender, MacroEventRecordedEventArgs args)
    {
        _recordedEventQueue.Enqueue(args.MacroEvent.DeepClone());
        Volatile.Write(ref _liveEventCount, args.EventCount);
    }

    private void OnSensitiveInputBlocked(object? sender, SensitiveInputBlockedEventArgs args)
    {
        Dispatch(() => SetBanner(args.Reason switch
        {
            SensitiveInputBlockReason.PasswordField => "Input over a password or credential field is being skipped.",
            SensitiveInputBlockReason.DifferentDesktop => "Input on the Windows secure desktop is not recorded.",
            _ => "Input capture is paused because the focused control could not be verified safely.",
        }));
    }

    private void OnPlaybackCompleted(object? sender, PlaybackCompletedEventArgs args)
    {
        if (args.Error is not null && args.Error is not OperationCanceledException)
        {
            _logger?.Error("playback_completion_error", args.Error);
        }
    }

    private void OnCommandExecutionFailed(object? sender, CommandExecutionFailedEventArgs args) =>
        EnterError("The requested action failed. " + args.Exception.Message, args.Exception);

    private void OnUiTimerTick(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (IsRecording)
        {
            var drained = 0;
            while (drained < 1_000 && _recordedEventQueue.TryDequeue(out var macroEvent))
            {
                Events.Add(new EventRowViewModel(macroEvent, Events.Count + 1));
                drained++;
            }

            if (DateTimeOffset.UtcNow - _lastRecoveryWrite >= RecoveryInterval)
            {
                _lastRecoveryWrite = DateTimeOffset.UtcNow;
                _ = SaveLiveRecoveryAsync();
            }
        }

        var progress = Interlocked.Exchange(ref _pendingPlaybackProgress, null);
        if (progress is not null)
        {
            _loopText = progress.TotalLoops is int total
                ? $"{progress.LoopNumber:N0} / {total:N0}"
                : $"{progress.LoopNumber:N0} / ∞";
            _remainingText = progress.EstimatedRemaining == Timeout.InfiniteTimeSpan
                ? "Until stopped"
                : FormatDuration(progress.EstimatedRemaining);
            OnPropertyChanged(nameof(LoopText));
            OnPropertyChanged(nameof(RemainingText));
        }

        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(EventCount));
        if (IsDirectionalHoldPlayback)
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ActiveHoldText));
            OnPropertyChanged(nameof(PhaseRemainingText));
        }
    }

    private async Task SaveLiveRecoveryAsync()
    {
        if (!await _recoveryWriteGate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var events = _recorder.Snapshot;
            if (events.Count == 0 || _recordingDisplayLayout is null)
            {
                return;
            }

            var snapshot = new MacroDocument
            {
                CreatedUtc = _document.CreatedUtc,
                DisplayLayout = _recordingDisplayLayout.DeepClone(),
                Events = events.Select(static item => item.DeepClone()).ToList(),
            };
            await _recovery.SaveAsync(snapshot).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or MacroValidationException)
        {
            _logger?.Warning("recovery_write_failed");
        }
        finally
        {
            _recoveryWriteGate.Release();
        }
    }

    private async Task SaveRecoverySnapshotAsync(MacroDocument document)
    {
        await _recoveryWriteGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _recovery.SaveAsync(document).ConfigureAwait(false);
        }
        finally
        {
            _recoveryWriteGate.Release();
        }
    }

    private async Task HandleRuntimeFailureAsync(string title, Exception exception)
    {
        try
        {
            if (_stateMachine.State == AppState.Recording)
            {
                var captured = _recorder.Stop();
                _activityStopwatch.Stop();
                _document = new MacroDocument
                {
                    CreatedUtc = _document.CreatedUtc,
                    DisplayLayout = _recordingDisplayLayout?.DeepClone() ?? _displayLayouts.Capture(),
                    Events = captured.Select(static item => item.DeepClone()).ToList(),
                };
                Volatile.Write(ref _liveEventCount, _document.Events.Count);
                while (_recordedEventQueue.TryDequeue(out _)) { }
                RebuildRows(_document);
                _history.Reset(_document);
                _isDirty = _document.Events.Count != 0;
                if (_document.Events.Count != 0)
                {
                    await SaveRecoverySnapshotAsync(_document).ConfigureAwait(true);
                }
            }
            else if (_stateMachine.State is AppState.Playing or AppState.Paused)
            {
                await _playback.StopAsync().ConfigureAwait(true);
            }
        }
        catch (Exception cleanupException)
        {
            exception = new AggregateException(exception, cleanupException);
        }

        EnterError(title + ". " + exception.Message, exception);
    }

    private void EnterError(string message, Exception? exception = null)
    {
        if (_stateMachine.State != AppState.Error)
        {
            _stateMachine.TryTransition(AppState.Error, message, out _);
        }

        _errorMessage = message;
        if (exception is not null)
        {
            _logger?.Error("application_error", exception);
        }
        else
        {
            _logger?.Warning("application_error");
        }

        RefreshAll();
    }

    private void SetBanner(string message)
    {
        _errorMessage = message;
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(WindowHeight));
        DismissErrorCommand.RaiseCanExecuteChanged();
    }

    private void ClearBanner()
    {
        _errorMessage = null;
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(WindowHeight));
        DismissErrorCommand.RaiseCanExecuteChanged();
    }

    private void DismissError()
    {
        if (_stateMachine.State == AppState.Error)
        {
            _stateMachine.TryResetError(out _);
        }

        ClearBanner();
        RefreshAll();
    }

    private void OnStateChanged(object? sender, AppStateChangedEventArgs args)
    {
        if (_dispatcher.CheckAccess())
        {
            RefreshStateProperties();
        }
        else
        {
            _dispatcher.BeginInvoke(RefreshStateProperties, DispatcherPriority.Background);
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(StateBrush));
    }

    private void RefreshAll()
    {
        RefreshStateProperties();
        RefreshDocumentProperties();
        OnPropertyChanged(nameof(AlwaysOnTop));
        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(WindowHeight));
        OnPropertyChanged(nameof(WindowWidth));
        OnPropertyChanged(nameof(WindowMinWidth));
        OnPropertyChanged(nameof(CountdownEnabled));
        OnPropertyChanged(nameof(ContinuousPlayback));
        OnPropertyChanged(nameof(LockMouseDuringDirectionalHold));
        OnPropertyChanged(nameof(MouseLockStatusText));
        OnPropertyChanged(nameof(PlaybackSpeed));
        OnPropertyChanged(nameof(RepeatCount));
        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(RecordHotkeyText));
        OnPropertyChanged(nameof(PlayHotkeyText));
        OnPropertyChanged(nameof(PauseHotkeyText));
        OnPropertyChanged(nameof(StopHotkeyText));
        OnPropertyChanged(nameof(HotkeyStatusText));
    }

    private void RefreshStateProperties()
    {
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PauseResumeLabel));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StateBrush));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(IsDirectionalHoldPlayback));
        OnPropertyChanged(nameof(ActiveHoldText));
        OnPropertyChanged(nameof(PhaseRemainingText));
        OnPropertyChanged(nameof(IsMouseLocked));
        OnPropertyChanged(nameof(MouseLockStatusText));
        OnPropertyChanged(nameof(RecordAutomationName));
        OnPropertyChanged(nameof(PlayAutomationName));
        RefreshCommands();
    }

    private void RefreshDocumentProperties()
    {
        OnPropertyChanged(nameof(EventCount));
        OnPropertyChanged(nameof(MacroDurationText));
        OnPropertyChanged(nameof(CurrentFileDisplay));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(DisplayLayoutStatus));
        OnPropertyChanged(nameof(LoopText));
        OnPropertyChanged(nameof(RemainingText));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        OpenCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        SaveAsCommand.RaiseCanExecuteChanged();
        RecordCommand.RaiseCanExecuteChanged();
        PlayCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        PauseResumeCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
        ApplyHotkeysCommand.RaiseCanExecuteChanged();
        SaveProfileCommand.RaiseCanExecuteChanged();
        LoadProfileCommand.RaiseCanExecuteChanged();
        DeleteProfileCommand.RaiseCanExecuteChanged();
        ToggleSettingsCommand.RaiseCanExecuteChanged();
        DismissErrorCommand.RaiseCanExecuteChanged();
        UndoCommand.RaiseCanExecuteChanged();
        RedoCommand.RaiseCanExecuteChanged();
        DeleteEventCommand.RaiseCanExecuteChanged();
        ClearAllEventsCommand.RaiseCanExecuteChanged();
        CreateDirectionalHoldPresetCommand.RaiseCanExecuteChanged();
        MoveEventUpCommand.RaiseCanExecuteChanged();
        MoveEventDownCommand.RaiseCanExecuteChanged();
        AddStepCommand.RaiseCanExecuteChanged();
        DuplicateStepCommand.RaiseCanExecuteChanged();
        DeleteStepCommand.RaiseCanExecuteChanged();
        MoveStepUpCommand.RaiseCanExecuteChanged();
        MoveStepDownCommand.RaiseCanExecuteChanged();
        RecordKeybindCommand.RaiseCanExecuteChanged();
        ClearKeybindCommand.RaiseCanExecuteChanged();
    }

    private bool CanUseFileCommands() => _initialized && !IsBusy && _stateMachine.State == AppState.Idle;
    private bool CanSave() => CanUseFileCommands() && (_isDirty || Events.Count > 0);
    private bool CanRecord() => _initialized && !IsCountingDown && _stateMachine.State is AppState.Idle or AppState.Recording;
    private bool CanPlay() => CanUseFileCommands() && Events.Any(static row => row.IsEnabled) && _stopRegistration is not null;
    private bool CanStop() => IsCountingDown || _stateMachine.State is AppState.Recording or AppState.Playing or AppState.Paused or AppState.Stopping;
    private bool CanExport() => CanUseFileCommands() && Events.Count > 0;
    private bool CanSaveProfile() => CanUseFileCommands() && Events.Count > 0 && IsValidProfileName(ProfileName);
    private bool CanUseSelectedProfile() => CanUseFileCommands() && FindProfileName(ProfileName) is not null;
    private bool CanClearAllEvents() => CanUseFileCommands() && Events.Count > 0;
    private bool CanUndo() => CanUseFileCommands() && _history.CanUndo;
    private bool CanRedo() => CanUseFileCommands() && _history.CanRedo;
    private bool CanEditSelectedEvent() => CanUseFileCommands() && SelectedEvent is not null;

    private static bool IsValidProfileName(string? name)
    {
        try
        {
            _ = ProfileService.ValidateName(name);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private string? FindProfileName(string? name) => Profiles.FirstOrDefault(
        profile => string.Equals(profile, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    private bool CanMoveSelectedEvent(int offset)
    {
        if (!CanEditSelectedEvent() || SelectedEvent is null)
        {
            return false;
        }

        var index = Events.IndexOf(SelectedEvent);
        var destination = index + offset;
        return index >= 0 && destination >= 0 && destination < Events.Count;
    }

    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    private void ToggleSettings()
    {
        IsSettingsOpen = !IsSettingsOpen;
        if (IsSettingsOpen)
        {
            IsExpanded = true;
        }
    }

    private void UpdateDisplayLayoutStatus()
    {
        if (_document.DisplayLayout is null)
        {
            _displayLayoutStatus = "This macro has no recorded display metadata.";
            return;
        }

        try
        {
            var comparison = _displayLayouts.CompareWithCurrent(_document.DisplayLayout);
            _displayLayoutStatus = comparison.IsEquivalent
                ? $"Display layout matches ({_document.DisplayLayout.Monitors.Count} monitor{(_document.DisplayLayout.Monitors.Count == 1 ? string.Empty : "s")})."
                : $"Display layout differs in {comparison.Differences.Count} setting{(comparison.Differences.Count == 1 ? string.Empty : "s")}.";
        }
        catch
        {
            _displayLayoutStatus = "The current display layout could not be compared.";
        }
    }

    private AppSettings CreateSettingsSnapshot() => new()
    {
        Version = AppSettings.CurrentVersion,
        WindowLeft = _windowLeft,
        WindowTop = _windowTop,
        AlwaysOnTop = AlwaysOnTop,
        IsExpanded = IsExpanded,
        CountdownEnabled = CountdownEnabled,
        PlaybackSpeed = PlaybackSpeed,
        RepeatCount = RepeatCount,
        ContinuousPlayback = ContinuousPlayback,
        LockMouseDuringDirectionalHold = LockMouseDuringDirectionalHold,
        Theme = Theme,
        RecentMacroPath = _settings.RecentMacroPath,
        RecordHotkey = ToSetting(_recordGesture),
        PlayHotkey = ToSetting(_playGesture),
        PauseHotkey = ToSetting(_pauseGesture),
        StopHotkey = ToSetting(_stopGesture),
    };

    private void DisposeHotkeys()
    {
        DisposeHotkeyRegistrations(throwOnFailure: false);
        if (_hotKeyService is not null)
        {
            _hotKeyService.HotKeyPressed -= OnHotKeyPressed;
            try
            {
                _hotKeyService.Dispose();
            }
            catch (Exception exception)
            {
                _logger?.Error("hotkey_shutdown_failed", exception);
            }

            _hotKeyService = null;
        }
    }

    private void DisposeHotkeyRegistrations(bool throwOnFailure)
    {
        List<Exception>? failures = null;
        foreach (var registration in new[] { _recordRegistration, _playRegistration, _pauseRegistration, _stopRegistration })
        {
            try
            {
                registration?.Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
                _logger?.Error("hotkey_unregister_failed", exception);
            }
        }

        _recordRegistration = null;
        _playRegistration = null;
        _pauseRegistration = null;
        _stopRegistration = null;
        if (throwOnFailure && failures is not null)
        {
            throw new AggregateException("One or more prior hotkeys could not be unregistered.", failures);
        }
    }

    private void DisposeServicesSynchronously()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _uiTimer.Stop();
        DisposeHotkeys();
        try
        {
            _recorder.Dispose();
        }
        finally
        {
            try
            {
                _inputCapture.Dispose();
            }
            finally
            {
                try
                {
                    _playback.Dispose();
                }
                finally
                {
                    try { _cursorLock.Dispose(); }
                    finally
                    {
                        _themeService.ThemeChanged -= OnThemeChanged;
                        _themeService.Dispose();
                    }
                }
            }
        }
    }

    private void Dispatch(Action action)
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        }
    }

    private void DispatchAsync(Func<Task> action)
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await action().ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                EnterError("A global control action failed. " + exception.Message, exception);
            }
        }), DispatcherPriority.Send);
    }

    private TimeSpan GetDocumentDuration()
    {
        decimal microseconds = 0;
        foreach (var row in Events)
        {
            microseconds += row.ToModel().DelayMicroseconds;
        }

        var ticks = Math.Min((decimal)TimeSpan.MaxValue.Ticks, microseconds * 10m);
        return TimeSpan.FromTicks((long)ticks);
    }

    private string GetSuggestedMacroName() =>
        string.IsNullOrWhiteSpace(_currentPath) ? "My macro" : Path.GetFileNameWithoutExtension(_currentPath);

    private string? GetCurrentDirectory() =>
        string.IsNullOrWhiteSpace(_currentPath) ? null : Path.GetDirectoryName(_currentPath);

    private static string BuildLayoutWarning(DisplayLayoutComparison comparison)
    {
        var lines = comparison.Differences.Take(6)
            .Select(static item => $"• {item.Property}: recorded {item.RecordedValue}; current {item.CurrentValue}");
        var suffix = comparison.Differences.Count > 6
            ? $"{Environment.NewLine}• …and {comparison.Differences.Count - 6} more difference(s)"
            : string.Empty;
        return "The monitor layout or display scaling differs from this recording:" + Environment.NewLine +
               string.Join(Environment.NewLine, lines) + suffix;
    }

    private static HotKeyGesture FromSetting(HotkeySetting? setting, HotKeyGesture fallback)
    {
        if (setting is null || setting.VirtualKey is 0 or > 0xFF)
        {
            return fallback;
        }

        const HotKeyModifiers allowed = HotKeyModifiers.Alt | HotKeyModifiers.Control | HotKeyModifiers.Shift | HotKeyModifiers.Windows;
        var modifiers = (HotKeyModifiers)setting.Modifiers & allowed;
        if (modifiers == HotKeyModifiers.None)
        {
            return fallback;
        }

        return new HotKeyGesture(modifiers | HotKeyModifiers.NoRepeat, setting.VirtualKey);
    }

    private static HotkeySetting ToSetting(HotKeyGesture gesture) => new()
    {
        Modifiers = (uint)(gesture.Modifiers | HotKeyModifiers.NoRepeat),
        VirtualKey = gesture.VirtualKey,
    };

    private static void EnsureDistinctHotkeys(params HotKeyGesture[] gestures)
    {
        var distinct = gestures
            .Select(static gesture => (gesture.Modifiers & ~HotKeyModifiers.NoRepeat, gesture.VirtualKey))
            .Distinct()
            .Count();
        if (distinct != gestures.Length)
        {
            throw new FormatException("Record, play, and emergency stop must use three different shortcuts.");
        }
    }

    private DirectionalHoldTimer GetDirectionalHoldTimer() =>
        DirectionalHoldPreset.GetTimer(_activityStopwatch.Elapsed, _activePlaybackSpeed);

    private void RefreshMouseLockProperties()
    {
        OnPropertyChanged(nameof(IsMouseLocked));
        OnPropertyChanged(nameof(MouseLockStatusText));
    }

    private static string FormatDirectionalHoldRemaining(TimeSpan remaining)
    {
        var totalSeconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
        return $"{totalSeconds / 60:00}:{totalSeconds % 60:00}";
    }

    private static string FormatElapsed(TimeSpan value) =>
        value.TotalHours >= 1
            ? value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);

    private static string FormatDuration(TimeSpan value)
    {
        if (value == Timeout.InfiniteTimeSpan)
        {
            return "Until stopped";
        }

        return value.TotalHours >= 1
            ? value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    private static Brush GetBrush(string resourceKey) =>
        Application.Current?.TryFindResource(resourceKey) as Brush ?? Brushes.DimGray;
}
