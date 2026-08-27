using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RelayLoop.Core;

namespace RelayLoop.Runner;

public partial class MainWindow : Window
{
    private readonly EmergencyStopHotkey _emergencyStopHotkey = new();
    private readonly StandaloneInputPlayer _player = new();
    private readonly DispatcherTimer _elapsedTimer;
    private readonly Stopwatch _playbackClock = new();
    private RunnerMacroData? _macro;
    private string? _hotkeyError;
    private bool _isClosing;
    private bool _closeApproved;
    private bool _closeInProgress;

    public MainWindow()
    {
        InitializeComponent();

        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _elapsedTimer.Tick += OnElapsedTimerTick;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            IntPtr window = new WindowInteropHelper(this).Handle;
            _emergencyStopHotkey.Register(window, OnEmergencyStopPressed);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            _hotkeyError = exception.Message;
            SetState(RunnerVisualState.Error, exception.Message);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await LoadEmbeddedMacroAsync();
    }

    private async Task LoadEmbeddedMacroAsync()
    {
        string? executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            SetState(RunnerVisualState.Error,
                "The runner could not locate its executable, so the embedded macro cannot be read.");
            return;
        }

        try
        {
            MacroDocument document = await RunnerPayloadCodec.ReadFromExecutableAsync(
                executablePath,
                CancellationToken.None);
            _macro = RunnerMacroAdapter.Create(document, executablePath);

            MacroNameText.Text = _macro.Name;
            EventCountText.Text = document.Events.Count.ToString("N0");
            DurationText.Text = FormatDuration(_macro.Duration);

            if (_hotkeyError is not null)
            {
                ConfirmCheckBox.IsEnabled = false;
                SetState(RunnerVisualState.Error, _hotkeyError);
                return;
            }

            ConfirmCheckBox.IsEnabled = true;
            SetState(RunnerVisualState.Ready, BuildReadyMessage(document));
            ConfirmCheckBox.Focus();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            SetState(RunnerVisualState.Error,
                $"No playable macro was found in this runner. {exception.Message}");
        }
    }

    private async void OnPlayClicked(object sender, RoutedEventArgs e)
    {
        if (_macro is null || ConfirmCheckBox.IsChecked != true || _hotkeyError is not null ||
            _player.IsPlaying || _player.HasUnreleasedInputs)
        {
            return;
        }

        PlayButton.IsEnabled = false;
        ConfirmCheckBox.IsEnabled = false;
        StopButton.IsEnabled = true;
        PlaybackProgress.Value = 0;
        PlaybackProgress.Visibility = Visibility.Visible;
        _playbackClock.Restart();
        _elapsedTimer.Start();
        SetState(RunnerVisualState.Playing, BuildPlayingMessage());

        try
        {
            Progress<double> progress = new(value => PlaybackProgress.Value = value);
            await _player.PlayAsync(_macro.Actions, progress, CancellationToken.None);

            if (_isClosing)
            {
                return;
            }

            _playbackClock.Stop();
            SetState(RunnerVisualState.Completed,
                $"Completed in {FormatDuration(_playbackClock.Elapsed)}. You may run the macro again or close this window.");
        }
        catch (RunnerInputReleaseException exception)
        {
            if (!_isClosing)
            {
                _playbackClock.Stop();
                RunnerInputReleaseException? remainingFailure = await _player.StopAsync();
                SetState(
                    RunnerVisualState.Error,
                    remainingFailure is null
                        ? "Playback stopped. Windows initially rejected a held-input release, but a retry succeeded. " +
                          "Verify the affected keys and mouse buttons before continuing."
                        : $"Playback stopped, but {remainingFailure.RemainingInputCount} input(s) may still be held. " +
                          "Press and release the affected keys or mouse buttons manually. " + exception.Message);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
            {
                _playbackClock.Stop();
                SetState(RunnerVisualState.Stopped,
                    $"Stopped safely at {FormatDuration(_playbackClock.Elapsed)}. Held keys and mouse buttons were released.");
            }
        }
        catch (Exception exception)
        {
            if (!_isClosing)
            {
                _playbackClock.Stop();
                SetState(RunnerVisualState.Error,
                    $"Playback failed. RelayLoop completed its held-input cleanup. {exception.Message}");
            }
        }
        finally
        {
            _elapsedTimer.Stop();
            if (!_isClosing)
            {
                StopButton.IsEnabled = false;
                ConfirmCheckBox.IsEnabled = _hotkeyError is null;
                PlayButton.IsEnabled = _hotkeyError is null &&
                                       ConfirmCheckBox.IsChecked == true &&
                                       !_player.HasUnreleasedInputs;
            }
        }
    }

    private async void OnStopClicked(object sender, RoutedEventArgs e) => await StopPlaybackAsync();

    private async void OnEmergencyStopPressed()
    {
        if (_player.IsPlaying)
        {
            await StopPlaybackAsync();
            return;
        }

        if (_macro is not null)
        {
            SetState(RunnerVisualState.Stopped,
                "Emergency stop is active. No playback was running; no input was sent.");
        }
    }

    private async Task StopPlaybackAsync()
    {
        StopButton.IsEnabled = false;
        SetState(RunnerVisualState.Stopped,
            "Stopping now… Held keys and mouse buttons are being released.");
        RunnerInputReleaseException? releaseFailure = await _player.StopAsync();
        if (!_isClosing && releaseFailure is not null)
        {
            SetState(
                RunnerVisualState.Error,
                $"Playback stopped, but {releaseFailure.RemainingInputCount} input(s) may still be held. " +
                "Press and release the affected keys or mouse buttons manually before continuing.");
        }
    }

    private void OnConfirmationChanged(object sender, RoutedEventArgs e)
    {
        PlayButton.IsEnabled = _macro is not null &&
                               _hotkeyError is null &&
                               ConfirmCheckBox.IsChecked == true &&
                               !_player.IsPlaying &&
                               !_player.HasUnreleasedInputs;
    }

    private void OnElapsedTimerTick(object? sender, EventArgs e)
    {
        if (_player.IsPlaying)
        {
            DetailText.Text = BuildPlayingMessage();
        }
    }

    private string BuildPlayingMessage() =>
        $"Playing • {FormatDuration(_playbackClock.Elapsed)} elapsed. Press Ctrl+Shift+Alt+S to stop immediately.";

    private static string BuildReadyMessage(MacroDocument document)
    {
        if (document.DisplayLayout is null)
        {
            return "Ready. The source display layout was not recorded; verify pointer positions before continuing.";
        }

        try
        {
            DisplayLayout current = RunnerDisplayLayout.Capture();
            IReadOnlyList<string> differences = RunnerDisplayLayout.Compare(document.DisplayLayout, current);
            if (differences.Count == 0)
            {
                return "Ready. Monitor bounds, primary display, and DPI match the recorded layout.";
            }

            string summary = string.Join("; ", differences.Take(3));
            if (differences.Count > 3)
            {
                summary += $"; plus {differences.Count - 3} more change(s)";
            }

            return "Caution: the current monitor or DPI layout differs from the recording. " +
                   $"Mouse coordinates may land on different controls. Detected: {summary}.";
        }
        catch (Exception exception)
        {
            return "Caution: RelayLoop could not verify the current monitor and DPI layout. " +
                   $"Check pointer positions before continuing. {exception.Message}";
        }
    }

    private void SetState(RunnerVisualState state, string detail)
    {
        string label;
        Brush brush;
        switch (state)
        {
            case RunnerVisualState.Ready:
                label = "READY";
                brush = FindBrush("AccentBrush");
                break;
            case RunnerVisualState.Playing:
                label = "PLAYING";
                brush = FindBrush("AccentBrush");
                break;
            case RunnerVisualState.Stopped:
                label = "STOPPED";
                brush = FindBrush("WarningBrush");
                break;
            case RunnerVisualState.Completed:
                label = "DONE";
                brush = FindBrush("AccentBrush");
                break;
            case RunnerVisualState.Error:
                label = "ERROR";
                brush = FindBrush("DangerBrush");
                break;
            default:
                label = "LOADING";
                brush = FindBrush("MutedInkBrush");
                break;
        }

        StateText.Text = label;
        StateText.Foreground = brush;
        StateDot.Fill = brush;
        DetailText.Text = detail;
    }

    private Brush FindBrush(string key) => (Brush)FindResource(key);

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalDays >= 1)
        {
            return $"{(long)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}";
        }

        if (value.TotalHours >= 1)
        {
            return value.ToString(@"h\:mm\:ss");
        }

        return value.ToString(@"m\:ss\.f");
    }

    internal void HandleFatalError(Exception exception)
    {
        RunnerInputReleaseException? releaseFailure = _player.StopAndRelease();
        StopButton.IsEnabled = false;
        PlayButton.IsEnabled = false;
        ConfirmCheckBox.IsEnabled = false;
        SetState(
            RunnerVisualState.Error,
            releaseFailure is null
                ? $"The runner stopped and released its tracked inputs after an unexpected error. {exception.Message}"
                : $"The runner stopped after an unexpected error, but {releaseFailure.RemainingInputCount} input(s) " +
                  $"may still be held. Release them manually. {exception.Message}");
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeApproved)
        {
            _elapsedTimer.Stop();
            _emergencyStopHotkey.Dispose();
            _player.Dispose();
            return;
        }

        e.Cancel = true;
        if (_closeInProgress)
        {
            return;
        }

        _closeInProgress = true;
        _isClosing = true;
        _elapsedTimer.Stop();
        RunnerInputReleaseException? releaseFailure = null;
        Exception? stopFailure = null;
        try
        {
            releaseFailure = await _player.StopAsync();
        }
        catch (Exception exception)
        {
            stopFailure = exception;
        }

        if (releaseFailure is not null || stopFailure is not null)
        {
            string detail = releaseFailure is not null
                ? $"Windows continued to reject {releaseFailure.RemainingInputCount} held-input release event(s)."
                : $"Cleanup could not be verified. {stopFailure!.Message}";
            MessageBox.Show(
                this,
                detail + Environment.NewLine + Environment.NewLine +
                "Press and release any affected keys or mouse buttons manually after the runner closes.",
                "RelayLoop Runner — input release warning",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        _closeApproved = true;
        _ = Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Normal);
    }

    private enum RunnerVisualState
    {
        Loading,
        Ready,
        Playing,
        Stopped,
        Completed,
        Error,
    }
}
